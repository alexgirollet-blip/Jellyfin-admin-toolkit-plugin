using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Storage;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>A movie whose versions Jellyfin merged on purpose (e.g. 1080p + 4K). Informational, not a problem.</summary>
public sealed class MergedVersionInfo
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int Versions { get; set; }

    public List<string> Files { get; set; } = new();

    public string? Drive { get; set; }
}

/// <summary>The cached result of a duplicate scan.</summary>
public sealed class DuplicateScanResult
{
    public DateTime ScannedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public int ItemsScanned { get; set; }

    public List<DuplicateGroup> Groups { get; set; } = new();

    public List<DuplicateGroup> EpisodeGroups { get; set; } = new();

    public List<MergedVersionInfo> MergedVersions { get; set; } = new();

    /// <summary>Gets or sets problems that made the result less complete (e.g. "Sonarr did not answer").</summary>
    public List<string> Problems { get; set; } = new();
}

/// <summary>Keep overrides and the removal list ("marked"), kept separately from the cache so a rescan keeps them.</summary>
public sealed class DuplicateChoices
{
    public Dictionary<string, Guid> Keep { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets group id -> item ids marked for removal.</summary>
    public Dictionary<string, List<Guid>> Marked { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What the admin asked to do with a duplicate group.</summary>
public sealed class RemovalRequest
{
    public string GroupId { get; set; } = string.Empty;

    public Guid KeepItemId { get; set; }

    public List<Guid> RemoveItemIds { get; set; } = new();

    /// <summary>Gets or sets "Auto" (Sonarr/Radarr when the copy is managed, else Jellyfin) or "LibraryOnly" (keep files).</summary>
    public string Method { get; set; } = "Auto";

    /// <summary>Gets or sets a value indicating whether unmanaged copies' files are deleted (needs an extra confirmation).</summary>
    public bool DeleteFiles { get; set; }

    /// <summary>Gets or sets a value indicating whether, for library-only removal, an empty .ignore file is written so the next scan does not add it back.</summary>
    public bool WriteIgnoreFile { get; set; }

    public bool CopyUserData { get; set; } = true;

    public bool DryRun { get; set; } = true;

    public string? Token { get; set; }

    public string? TypedConfirmation { get; set; }
}

/// <summary>One step of a removal plan.</summary>
public sealed class PlanStep
{
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets CopyUserData, ArrDelete, JellyfinDeleteFiles, JellyfinRemove, WriteIgnore.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Paths { get; set; } = new();

    public long Bytes { get; set; }

    public bool Possible { get; set; } = true;

    public string? Blocker { get; set; }

    public List<string> ManualCommands { get; set; } = new();

    public bool Destructive { get; set; }

    /// <summary>Gets or sets the result after execution: Done, Failed, Skipped, WouldDo.</summary>
    public string? Result { get; set; }

    public string? ResultDetail { get; set; }

    internal int? ArrId { get; set; }

    internal MediaKind ArrKindOf { get; set; }

    internal List<(Guid UserId, Guid FromItem, Guid ToItem, PlayState State)> UserDataCopies { get; set; } = new();
}

/// <summary>A removal preview, or the outcome of executing it.</summary>
public sealed class RemovalPlan
{
    public string GroupId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public Guid KeepItemId { get; set; }

    public List<PlanStep> Steps { get; set; } = new();

    public long BytesFreed { get; set; }

    public bool CanExecute { get; set; }

    public bool Destructive { get; set; }

    public bool NeedsTypedDelete { get; set; }

    public List<string> Warnings { get; set; } = new();

    public string? Token { get; set; }

    public bool SafeMode { get; set; }

    public bool Executed { get; set; }

    public bool DryRun { get; set; }

    internal string Fingerprint { get; set; } = string.Empty;
}

/// <summary>Scans for duplicates, keeps the cached result, and performs previewed removals.</summary>
public sealed class DuplicateService : IDisposable
{
    public const string CacheFile = "duplicates.json";
    public const string ChoicesFile = "choices.json";

    private readonly ILibraryAdapter _library;
    private readonly IUserAdapter _users;
    private readonly IDriveSpaceProvider _space;
    private readonly ArrFactory _arr;
    private readonly JsonStore _store;
    private readonly ActivityLog _log;
    private readonly SafetyGuard _guard;
    private readonly Func<PluginConfiguration> _config;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public DuplicateService(ILibraryAdapter library, IUserAdapter users, IDriveSpaceProvider space, ArrFactory arr, JsonStore store, ActivityLog log, SafetyGuard guard, Func<PluginConfiguration> config)
    {
        _library = library;
        _users = users;
        _space = space;
        _arr = arr;
        _store = store;
        _log = log;
        _guard = guard;
        _config = config;
    }

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public void Dispose() => _scanLock.Dispose();

    public DuplicateScanResult? GetCached() => _store.Read<DuplicateScanResult>(CacheFile);

    public DuplicateChoices GetChoices() => _store.Read<DuplicateChoices>(ChoicesFile) ?? new DuplicateChoices();

    public async Task<DuplicateScanResult> ScanAsync(string user, IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new ToolkitException("busy", "A duplicate scan is already running.", 409);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => Scan(progress, ct), ct).ConfigureAwait(false);
            result.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            _store.Write(CacheFile, result);
            _log.Add(user, "Duplicates", "Scan", "Library", "OK", string.Create(CultureInfo.InvariantCulture, $"{result.ItemsScanned} items, {result.Groups.Count} duplicate groups, {result.EpisodeGroups.Count} episode duplicates, {result.MergedVersions.Count} merged multi-version movies in {result.DurationSeconds}s. {string.Join(" ", result.Problems)}").Trim());
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolkitException)
        {
            _log.Add(user, "Duplicates", "Scan", "Library", "Failed", ErrorText.Describe(ex));
            throw;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Changes which copy is kept (one-click override) and returns the re-evaluated group.</summary>
    public DuplicateGroup SetKeep(string groupId, Guid keepId, string user)
    {
        var cache = GetCached() ?? throw new ToolkitException("no_scan", "Run a duplicate scan first.", 404);
        var group = FindGroup(cache, groupId);
        DuplicateScorer.Evaluate(group, keepId, Context(group.Kind, cache));
        var choices = GetChoices();
        if (keepId == group.RecommendedKeepId)
        {
            choices.Keep.Remove(groupId);
        }
        else
        {
            choices.Keep[groupId] = keepId;
        }

        if (choices.Marked.ContainsKey(groupId))
        {
            choices.Marked[groupId] = group.Copies.Where(c => c.Item.Id != keepId).Select(c => c.Item.Id).ToList();
        }

        _store.Write(ChoicesFile, choices);
        _store.Write(CacheFile, cache);
        _log.Add(user, "Duplicates", "Choose copy to keep", group.Title, "OK", keepId == group.RecommendedKeepId ? "Back to the recommendation." : "Overrode the recommendation: keep " + Describe(group.Copies.First(c => c.Item.Id == keepId)));
        return group;
    }

    /// <summary>Adds groups to (or removes them from) the removal report. Never deletes anything.</summary>
    public DuplicateChoices Mark(IReadOnlyList<string> groupIds, bool marked, string user)
    {
        var cache = GetCached() ?? throw new ToolkitException("no_scan", "Run a duplicate scan first.", 404);
        var choices = GetChoices();
        foreach (var id in groupIds)
        {
            var g = FindGroup(cache, id);
            if (marked)
            {
                choices.Marked[id] = g.Copies.Where(c => c.Item.Id != g.KeepId).Select(c => c.Item.Id).ToList();
            }
            else
            {
                choices.Marked.Remove(id);
            }
        }

        _store.Write(ChoicesFile, choices);
        _log.Add(user, "Duplicates", marked ? "Mark for removal" : "Unmark", string.Join(", ", groupIds.Take(20)), "OK", string.Create(CultureInfo.InvariantCulture, $"{groupIds.Count} group(s); nothing deleted."));
        return choices;
    }

    /// <summary>The removal list: marked copies (or, when <paramref name="recommended"/>, every auto-selected recommendation).</summary>
    public List<(DuplicateGroup Group, DuplicateCopy Copy)> RemovalList(bool recommended)
    {
        var cache = GetCached();
        if (cache is null)
        {
            return new();
        }

        var choices = GetChoices();
        var list = new List<(DuplicateGroup, DuplicateCopy)>();
        foreach (var g in cache.Groups.Concat(cache.EpisodeGroups))
        {
            IEnumerable<DuplicateCopy> remove = recommended
                ? (g.AutoSelect ? g.Copies.Where(c => c.Item.Id != g.KeepId) : Enumerable.Empty<DuplicateCopy>())
                : choices.Marked.TryGetValue(g.Id, out var ids) ? g.Copies.Where(c => ids.Contains(c.Item.Id)) : Enumerable.Empty<DuplicateCopy>();
            list.AddRange(remove.Select(c => (g, c)));
        }

        return list;
    }

    public string ExportCsv(bool recommended)
    {
        var rows = RemovalList(recommended).Select(x => new object?[]
        {
            x.Group.Id, x.Group.Kind, x.Group.Title, x.Group.Year, x.Group.Confidence, x.Copy.Item.Id.ToString("N", CultureInfo.InvariantCulture),
            x.Copy.Item.FolderPath ?? x.Copy.Item.Path, x.Copy.Drive, x.Copy.Item.LibraryName, x.Copy.TotalSizeBytes,
            Math.Round(x.Copy.TotalSizeBytes / 1e9, 2), x.Copy.ArrManaged, x.Copy.ArrPath, x.Copy.EpisodeCount, x.Copy.Score,
            x.Group.Copies.First(c => c.Item.Id == x.Group.KeepId).Item.FolderPath, string.Join(" | ", x.Copy.Reasons)
        });
        return Csv.Write(
            new[] { "group", "kind", "title", "year", "confidence", "itemId", "path", "drive", "library", "sizeBytes", "sizeGB", "managedByArr", "arrPath", "episodes", "score", "keptCopyPath", "reasons" },
            rows);
    }

    public object ExportJson(bool recommended)
        => RemovalList(recommended).Select(x => new
        {
            group = x.Group.Id,
            kind = x.Group.Kind.ToString(),
            title = x.Group.Title,
            year = x.Group.Year,
            confidence = x.Group.Confidence.ToString(),
            itemId = x.Copy.Item.Id,
            path = x.Copy.Item.FolderPath ?? x.Copy.Item.Path,
            files = x.Copy.Item.FilePaths,
            drive = x.Copy.Drive,
            library = x.Copy.Item.LibraryName,
            sizeBytes = x.Copy.TotalSizeBytes,
            managedByArr = x.Copy.ArrManaged,
            arrPath = x.Copy.ArrPath,
            keptCopyPath = x.Group.Copies.First(c => c.Item.Id == x.Group.KeepId).Item.FolderPath,
            reasons = x.Copy.Reasons
        }).ToList();

    /// <summary>Builds the exact list of what removing the chosen copies would do. Changes nothing.</summary>
    public async Task<RemovalPlan> PlanAsync(RemovalRequest req, Guid userId, CancellationToken ct)
    {
        var plan = await BuildPlanAsync(req, ct).ConfigureAwait(false);
        plan.SafeMode = _guard.SafeMode;
        if (plan.CanExecute)
        {
            plan.Token = _guard.IssueToken(plan.Fingerprint, userId);
        }

        return plan;
    }

    /// <summary>Executes a previewed plan (or simulates it with DryRun). Re-checks everything and refuses if anything changed.</summary>
    public async Task<RemovalPlan> ExecuteAsync(RemovalRequest req, Guid userId, string user, CancellationToken ct)
    {
        if (!req.DryRun)
        {
            _guard.EnsureChangesAllowed("remove duplicates");
        }

        var plan = await BuildPlanAsync(req, ct).ConfigureAwait(false);
        if (!plan.CanExecute)
        {
            throw new ToolkitException("plan_blocked", "This removal cannot run as previewed: " + string.Join(" ", plan.Steps.Where(s => !s.Possible).Select(s => s.Blocker)), 409);
        }

        _guard.ConsumeToken(req.Token, plan.Fingerprint, userId, plan.Destructive && !req.DryRun, req.TypedConfirmation);
        plan.DryRun = req.DryRun;
        if (req.DryRun)
        {
            foreach (var s in plan.Steps)
            {
                s.Result = "WouldDo";
            }

            _log.Add(user, "Duplicates", "Remove (dry run)", plan.Title, "DryRun", Summary(plan));
            return plan;
        }

        bool failed = false;
        var removed = new List<Guid>();
        foreach (var step in plan.Steps)
        {
            if (failed)
            {
                step.Result = "Skipped";
                step.ResultDetail = "Skipped because an earlier step failed.";
                continue;
            }

            try
            {
                await RunStepAsync(step, ct).ConfigureAwait(false);
                step.Result = "Done";
                if (step.Kind is "JellyfinRemove" or "JellyfinDeleteFiles")
                {
                    removed.Add(step.ItemId);
                }

                _log.Add(user, "Duplicates", StepAction(step), plan.Title, "OK", step.Description + (step.ResultDetail is null ? string.Empty : " " + step.ResultDetail));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                step.Result = "Failed";
                step.ResultDetail = ErrorText.Describe(ex);
                _log.Add(user, "Duplicates", StepAction(step), plan.Title, "Failed", step.Description + " Error: " + step.ResultDetail);
            }
        }

        plan.Executed = true;
        ForgetRemoved(plan.GroupId, removed);
        return plan;
    }

    internal static string Fingerprint(RemovalPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append(plan.GroupId).Append('|').Append(plan.KeepItemId);
        foreach (var s in plan.Steps)
        {
            sb.Append('|').Append(s.Kind).Append(':').Append(s.ItemId).Append(':').Append(s.ArrId).Append(':').Append(s.Possible).Append(':').Append(string.Join(";", s.Paths));
            foreach (var u in s.UserDataCopies)
            {
                sb.Append(':').Append(u.UserId).Append('>').Append(u.ToItem);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string StepAction(PlanStep s) => s.Kind switch
    {
        "CopyUserData" => "Copy watch data",
        "ArrDelete" => "Delete via Sonarr/Radarr",
        "JellyfinDeleteFiles" => "Delete files",
        "JellyfinRemove" => "Remove from library",
        "WriteIgnore" => "Write .ignore",
        _ => s.Kind
    };

    private static string Summary(RemovalPlan p)
        => string.Join(" ", p.Steps.Select(s => s.Description)) + string.Create(CultureInfo.InvariantCulture, $" Space freed: {p.BytesFreed / 1e9:0.00} GB.");

    private static string Describe(DuplicateCopy c) => (c.Item.FolderPath ?? c.Item.Path ?? c.Item.Id.ToString()) + (c.Drive is null ? string.Empty : " [" + c.Drive + "]");

    private static DuplicateGroup FindGroup(DuplicateScanResult cache, string groupId)
        => cache.Groups.Concat(cache.EpisodeGroups).FirstOrDefault(g => g.Id == groupId)
           ?? throw new ToolkitException("not_found", "That duplicate group is not in the last scan. Run the scan again.", 404);

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return 0;
    }

    private static bool Exists(string? path) => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    private ScoringContext Context(MediaKind kind, DuplicateScanResult? cache)
    {
        var cfg = _config();
        bool arrDown = cache?.Problems.Any(p => p.StartsWith(kind == MediaKind.Series ? "Sonarr" : "Radarr", StringComparison.Ordinal)) ?? false;
        var client = kind == MediaKind.Series ? _arr.Sonarr() : _arr.Radarr();
        return new ScoringContext
        {
            Weights = cfg.Weights,
            PreferredDrive = cfg.PreferredDrive,
            ArrAvailable = kind is MediaKind.Movie or MediaKind.Series && client.IsConfigured && !arrDown,
            ArrName = kind == MediaKind.Series ? "Sonarr" : kind == MediaKind.Movie ? "Radarr" : "Sonarr/Radarr"
        };
    }

    private DuplicateScanResult Scan(IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = _config();
        var result = new DuplicateScanResult { ScannedUtc = DateTime.UtcNow };
        var items = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Movie, MediaKind.Series }, WithStreams = true });
        result.ItemsScanned = items.Count;
        progress?.Report(10);

        var arrTitles = new Dictionary<MediaKind, IReadOnlyList<ArrTitle>?>();
        foreach (var kind in new[] { MediaKind.Series, MediaKind.Movie })
        {
            var client = _arr.For(kind);
            if (!client.IsConfigured)
            {
                arrTitles[kind] = null;
                continue;
            }

            try
            {
                arrTitles[kind] = client.ListAsync(ct).GetAwaiter().GetResult();
            }
            catch (ArrException ex)
            {
                arrTitles[kind] = null;
                result.Problems.Add($"{client.Name}: {ex.Message} The managed-path signal was not used for {(kind == MediaKind.Series ? "series" : "movies")}.");
            }
        }

        var drives = new DriveMapper(cfg.DriveMappings);
        var users = _users.GetUsers();
        var choices = GetChoices();
        var byId = items.ToDictionary(i => i.Id);
        var candidates = DuplicateDetector.Find(items);
        int done = 0;
        foreach (var cand in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var group = BuildGroup(cand, cand.ItemIds.Select(id => byId[id]).ToList(), arrTitles.GetValueOrDefault(cand.Kind), drives, users, choices);
            DuplicateScorer.Score(group, Context(group.Kind, result));
            result.Groups.Add(group);
            progress?.Report(10 + (70.0 * ++done / Math.Max(1, candidates.Count)));
        }

        result.MergedVersions = items.Where(i => i.Kind == MediaKind.Movie && i.VersionCount > 1)
            .Select(i => new MergedVersionInfo { ItemId = i.Id, Name = i.Name, Year = i.Year, Versions = i.VersionCount, Files = i.FilePaths, Drive = drives.LabelFor(i.Path) })
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cfg.ScanEpisodeDuplicates)
        {
            var series = items.Where(i => i.Kind == MediaKind.Series).ToList();
            int n = 0;
            foreach (var s in series)
            {
                ct.ThrowIfCancellationRequested();
                var eps = _library.GetEpisodes(s.Id, false);
                foreach (var cand in DuplicateDetector.FindEpisodeDuplicates(eps))
                {
                    var epItems = cand.ItemIds.Select(id => eps.First(e => e.Id == id)).ToList();
                    var group = BuildGroup(cand, epItems, null, drives, users, choices);
                    group.Title = $"{s.Name}: {EpisodeOverlap.Key(epItems[0].SeasonNumber ?? 0, epItems[0].EpisodeNumber ?? 0)}";
                    group.Year = s.Year;
                    group.SeriesId = s.Id;
                    group.PosterItemId = s.Id;
                    group.Notes.Add("Episode-level duplicates are not checked against Sonarr; removing one only affects that episode file.");
                    DuplicateScorer.Score(group, Context(MediaKind.Episode, result));
                    result.EpisodeGroups.Add(group);
                }

                progress?.Report(80 + (20.0 * ++n / Math.Max(1, series.Count)));
            }
        }

        result.Groups = result.Groups.OrderBy(g => g.Confidence).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(100);
        return result;
    }

    private DuplicateGroup BuildGroup(CandidateGroup cand, List<ItemSnapshot> items, IReadOnlyList<ArrTitle>? arrTitles, DriveMapper drives, IReadOnlyList<UserRef> users, DuplicateChoices choices)
    {
        var group = new DuplicateGroup
        {
            Id = DuplicateDetector.GroupId(cand.ItemIds),
            Kind = cand.Kind,
            Title = items[0].Name,
            Year = items.Select(i => i.Year).FirstOrDefault(y => y is not null),
            Signals = cand.Signals.OrderBy(s => s).ToList(),
            Evidence = cand.Evidence
        };
        var episodeKeys = new Dictionary<Guid, HashSet<string>>();
        var spaceCache = new Dictionary<string, DriveSpace?>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var copy = new DuplicateCopy { Item = item };
            var folder = item.FolderPath ?? item.Path;
            var mapping = drives.Find(folder);
            copy.Drive = mapping?.Label;
            if (mapping is not null)
            {
                if (!spaceCache.TryGetValue(mapping.Prefix, out var space))
                {
                    spaceCache[mapping.Prefix] = space = _space.GetSpace(mapping.Prefix);
                }

                copy.DriveFreeBytes = space?.FreeBytes;
                copy.DriveTotalBytes = space?.TotalBytes;
            }

            copy.PathExists = Exists(item.Kind == MediaKind.Series ? item.Path : item.Path ?? folder);
            if (item.Kind == MediaKind.Series)
            {
                FillSeries(copy, episodeKeys, users);
            }
            else
            {
                var files = item.FilePaths.Count > 0 ? item.FilePaths : (item.Path is null ? new List<string>() : new List<string> { item.Path });
                copy.FileCount = files.Count;
                copy.FilesMissing = files.Count(f => !File.Exists(f));
                copy.TotalSizeBytes = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                if (copy.TotalSizeBytes == 0)
                {
                    copy.TotalSizeBytes = item.SizeBytes ?? 0;
                }

                foreach (var u in users)
                {
                    var st = _users.GetPlayState(u.Id, item.Id);
                    if (st is not null && st.HasAnyData)
                    {
                        copy.Watches.Add(new CopyWatch { UserId = u.Id, UserName = u.Name, PlayedCount = st.Played ? Math.Max(1, st.PlayCount) : 0, Favorite = st.IsFavorite, HasResume = st.PlaybackPositionTicks > 0, LastPlayed = st.LastPlayedDate });
                    }
                }
            }

            if (arrTitles is not null && item.Kind is MediaKind.Movie or MediaKind.Series)
            {
                var kind = item.Kind == MediaKind.Series ? ArrKind.Sonarr : ArrKind.Radarr;
                copy.ArrService = kind.ToString();
                var match = ArrClient.Match(arrTitles, item, kind);
                if (match is null)
                {
                    copy.ArrManaged = false;
                    copy.ArrProblem = $"not found in {kind} (by TVDb/TMDb/IMDb id)";
                }
                else
                {
                    copy.ArrId = match.Id;
                    copy.ArrPath = match.Path;
                    copy.ArrManaged = PathText.SamePath(match.Path, folder) || (item.Kind == MediaKind.Movie && PathText.IsUnder(item.Path, match.Path));
                }
            }

            group.Copies.Add(copy);
        }

        // Same folder in two libraries (or nested folders): deleting files of one would delete the other.
        for (int a = 0; a < group.Copies.Count; a++)
        {
            for (int b = a + 1; b < group.Copies.Count; b++)
            {
                var fa = group.Copies[a].Item.Kind == MediaKind.Episode ? group.Copies[a].Item.Path : group.Copies[a].Item.FolderPath;
                var fb = group.Copies[b].Item.Kind == MediaKind.Episode ? group.Copies[b].Item.Path : group.Copies[b].Item.FolderPath;
                if (PathText.IsUnder(fa, fb) || PathText.IsUnder(fb, fa) || group.Copies[a].Item.FilePaths.Intersect(group.Copies[b].Item.FilePaths).Any())
                {
                    group.Copies[a].SharesFilesWithAnotherCopy = true;
                    group.Copies[b].SharesFilesWithAnotherCopy = true;
                }
            }
        }

        if (group.Kind == MediaKind.Movie && group.Copies.Count(c => c.Item.FolderPath is not null) > 1
            && group.Copies.Select(c => PathText.Normalize(c.Item.FolderPath)).Distinct().Count() == 1)
        {
            group.Notes.Add("The copies are files in the same folder that Jellyfin did not merge as versions. Rename them to Jellyfin's multi-version naming (\"Movie (Year) - 1080p.mkv\") if you want to keep both.");
        }

        if (episodeKeys.Count > 1)
        {
            foreach (var cov in EpisodeOverlap.Compute(episodeKeys))
            {
                group.Copies.First(c => c.Item.Id == cov.ItemId).Coverage = cov;
            }

            int union = episodeKeys.Values.SelectMany(v => v).Distinct().Count();
            foreach (var c in group.Copies.Where(c => c.ExpectedEpisodes is null))
            {
                c.ExpectedEpisodes = union;
                c.ExpectedSource = "all copies together";
            }

            int expected = group.Copies.Max(c => c.ExpectedEpisodes ?? 0);
            var shared = group.Copies.Min(c => c.Coverage?.SharedWithAll ?? 0);
            group.Evidence.Add(string.Create(CultureInfo.InvariantCulture, $"Episode overlap: {shared} episode(s) in every copy; {string.Join(", ", group.Copies.Select(c => $"{c.EpisodeCount} in copy on {c.Drive ?? "?"}"))}; expected {expected}."));
        }

        if (choices.Keep.TryGetValue(group.Id, out var keep) && group.Copies.Any(c => c.Item.Id == keep))
        {
            group.KeepId = keep;
        }

        group.PosterItemId = group.KeepId != Guid.Empty ? group.KeepId : items[0].Id;
        return group;
    }

    private void FillSeries(DuplicateCopy copy, Dictionary<Guid, HashSet<string>> episodeKeys, IReadOnlyList<UserRef> users)
    {
        var all = _library.GetEpisodes(copy.Item.Id, true);
        var present = all.Where(e => !e.IsVirtual).ToList();
        var keys = new HashSet<string>(present.SelectMany(EpisodeOverlap.Keys), StringComparer.Ordinal);
        episodeKeys[copy.Item.Id] = keys;
        copy.EpisodeCount = keys.Count;
        var missing = new HashSet<string>(all.Where(e => e.IsVirtual).SelectMany(e => EpisodeOverlap.Keys(new ItemSnapshot { SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, EpisodeNumberEnd = e.EpisodeNumberEnd })), StringComparer.Ordinal);
        missing.ExceptWith(keys);
        if (missing.Count > 0)
        {
            copy.ExpectedEpisodes = keys.Count + missing.Count;
            copy.ExpectedSource = "Jellyfin's missing-episode data";
        }

        var files = present.Select(e => e.Path).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        copy.FileCount = files.Count;
        copy.FilesMissing = files.Count(f => !File.Exists(f));
        copy.TotalSizeBytes = files.Where(File.Exists).Sum(f => new FileInfo(f!).Length);
        if (copy.TotalSizeBytes == 0)
        {
            copy.TotalSizeBytes = present.Sum(e => e.SizeBytes ?? 0);
        }

        // Quality of a series = the most common resolution of its episodes.
        var common = present.Where(e => e.Height is not null)
            .GroupBy(e => DuplicateScorer.ResolutionClass(e.Width, e.Height))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.First();
        if (common is not null)
        {
            copy.Item.Width = common.Width;
            copy.Item.Height = common.Height;
            copy.Item.VideoCodec = common.VideoCodec;
            copy.Item.VideoRange = common.VideoRange;
            copy.Item.BitDepth = common.BitDepth;
            copy.Item.Bitrate = common.Bitrate;
        }

        copy.Item.AudioLanguages = present.SelectMany(e => e.AudioLanguages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        copy.Item.SubtitleLanguages = present.SelectMany(e => e.SubtitleLanguages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var u in users)
        {
            int played = 0;
            bool resume = false;
            DateTime? last = null;
            foreach (var e in present)
            {
                var st = _users.GetPlayState(u.Id, e.Id);
                if (st is null || !st.HasAnyData)
                {
                    continue;
                }

                played += st.Played ? 1 : 0;
                resume |= st.PlaybackPositionTicks > 0;
                if (st.LastPlayedDate is not null && (last is null || st.LastPlayedDate > last))
                {
                    last = st.LastPlayedDate;
                }
            }

            var fav = _users.GetPlayState(u.Id, copy.Item.Id)?.IsFavorite ?? false;
            if (played > 0 || resume || fav || last is not null)
            {
                copy.Watches.Add(new CopyWatch { UserId = u.Id, UserName = u.Name, PlayedCount = played, Favorite = fav, HasResume = resume, LastPlayed = last });
            }
        }
    }

    private async Task<RemovalPlan> BuildPlanAsync(RemovalRequest req, CancellationToken ct)
    {
        var cache = GetCached() ?? throw new ToolkitException("no_scan", "Run a duplicate scan first.", 404);
        var group = FindGroup(cache, req.GroupId);
        if (group.Copies.All(c => c.Item.Id != req.KeepItemId))
        {
            throw new ToolkitException("bad_keep", "The copy to keep is not part of this group.");
        }

        if (req.RemoveItemIds.Count == 0 || req.RemoveItemIds.Contains(req.KeepItemId) || req.RemoveItemIds.Any(id => group.Copies.All(c => c.Item.Id != id)))
        {
            throw new ToolkitException("bad_remove", "Choose at least one copy to remove; the kept copy cannot be removed.");
        }

        var cfg = _config();
        var plan = new RemovalPlan { GroupId = group.Id, Title = group.Title + (group.Year is null ? string.Empty : $" ({group.Year})"), KeepItemId = req.KeepItemId, DryRun = req.DryRun };
        var keeper = _library.GetItem(req.KeepItemId, false) ?? throw new ToolkitException("not_found", "The copy to keep no longer exists in Jellyfin. Run the scan again.", 404);
        if (!Exists(keeper.Kind == MediaKind.Series ? keeper.Path : keeper.Path ?? keeper.FolderPath))
        {
            plan.Warnings.Add($"The copy you keep is not on disk right now ({keeper.Path}). Check the drive is mounted before removing anything.");
        }

        IReadOnlyList<ArrTitle>? arrTitles = null;
        var arrClient = _arr.For(group.Kind);
        bool libraryOnly = string.Equals(req.Method, "LibraryOnly", StringComparison.OrdinalIgnoreCase);
        if (!libraryOnly && group.Kind is MediaKind.Movie or MediaKind.Series && arrClient.IsConfigured)
        {
            try
            {
                arrTitles = await arrClient.ListAsync(ct).ConfigureAwait(false);
            }
            catch (ArrException ex)
            {
                plan.Warnings.Add($"{arrClient.Name} could not be checked: {ex.Message} Removal is planned as if no copy were managed.");
            }
        }

        var users = req.CopyUserData ? _users.GetUsers() : Array.Empty<UserRef>();
        foreach (var removeId in req.RemoveItemIds)
        {
            var item = _library.GetItem(removeId, false);
            if (item is null)
            {
                plan.Steps.Add(new PlanStep { ItemId = removeId, Kind = "JellyfinRemove", Description = "Already gone from Jellyfin.", Possible = false, Blocker = "This copy no longer exists in Jellyfin. Run the scan again." });
                continue;
            }

            var cached = group.Copies.First(c => c.Item.Id == removeId);
            var where = (item.FolderPath ?? item.Path) + (cached.Drive is null ? string.Empty : $" [{cached.Drive}]");

            if (req.CopyUserData)
            {
                var step = PlanUserData(item, keeper, users);
                if (step is not null)
                {
                    plan.Steps.Add(step);
                }
            }

            var deletePaths = item.DeletePaths.Count > 0 ? item.DeletePaths : new List<string> { item.FolderPath ?? item.Path ?? string.Empty };
            var match = arrTitles is null ? null : ArrClient.Match(arrTitles, item, group.Kind == MediaKind.Series ? ArrKind.Sonarr : ArrKind.Radarr);
            bool managed = match is not null && (PathText.SamePath(match.Path, item.FolderPath ?? item.Path) || (item.Kind == MediaKind.Movie && PathText.IsUnder(item.Path, match.Path)));

            if (managed)
            {
                long bytes = deletePaths.Sum(SizeOf);
                plan.Steps.Add(new PlanStep
                {
                    ItemId = removeId,
                    Kind = "ArrDelete",
                    ArrId = match!.Id,
                    ArrKindOf = group.Kind,
                    Destructive = true,
                    Paths = new List<string> { match.Path },
                    Bytes = bytes,
                    Description = $"Delete \"{match.Title}\" in {arrClient.Name} with its files ({match.RawPath}). {arrClient.Name} will no longer track it; no import exclusion is added."
                });
                plan.Steps.Add(new PlanStep { ItemId = removeId, Kind = "JellyfinRemove", Paths = new List<string> { item.FolderPath ?? item.Path ?? string.Empty }, Description = $"Remove the copy at {where} from the Jellyfin library ({arrClient.Name} deletes the files)." });
                plan.Warnings.Add($"The copy at {where} is the one {arrClient.Name} manages. After this, {arrClient.Name} will not know about the copy you keep; add it again in {arrClient.Name} if you want it monitored.");
                continue;
            }

            bool delete = req.DeleteFiles && !libraryOnly;
            if (delete && cached.SharesFilesWithAnotherCopy)
            {
                plan.Steps.Add(new PlanStep { ItemId = removeId, Kind = "JellyfinDeleteFiles", Paths = deletePaths, Destructive = true, Possible = false, Description = $"Delete files at {where}.", Blocker = "This copy uses the same files as another copy; deleting them would delete the kept copy too. Use \"Remove from library only\"." });
                continue;
            }

            if (delete)
            {
                var perm = PermissionProbe.CheckDeletable(deletePaths);
                long bytes = deletePaths.Sum(SizeOf);
                plan.Steps.Add(new PlanStep
                {
                    ItemId = removeId,
                    Kind = "JellyfinDeleteFiles",
                    Destructive = true,
                    Paths = deletePaths,
                    Bytes = bytes,
                    Possible = perm.Ok,
                    Blocker = perm.Ok ? null : perm.Message + " Nothing was changed. You can run the command below yourself, or choose \"Remove from library only\".",
                    ManualCommands = perm.Ok ? new List<string>() : PermissionProbe.ManualDeleteCommands(deletePaths, cfg.ContainerName),
                    Description = $"Remove from Jellyfin and delete these files/folders from disk: {string.Join(", ", deletePaths)}."
                });
                continue;
            }

            plan.Steps.Add(new PlanStep { ItemId = removeId, Kind = "JellyfinRemove", Paths = new List<string> { item.FolderPath ?? item.Path ?? string.Empty }, Description = $"Remove the copy at {where} from the Jellyfin library. Files are kept." });
            var folder = item.Kind == MediaKind.Episode ? null : item.FolderPath ?? item.Path;
            if (req.WriteIgnoreFile && folder is not null)
            {
                var perm = PermissionProbe.CheckWritableDirectory(folder);
                plan.Steps.Add(new PlanStep
                {
                    ItemId = removeId,
                    Kind = "WriteIgnore",
                    Paths = new List<string> { Path.Combine(folder, ".ignore") },
                    Possible = perm.Ok,
                    Blocker = perm.Ok ? null : perm.Message + " Untick \".ignore\" to remove from the library only (the next library scan will add it back).",
                    ManualCommands = perm.Ok ? new List<string>() : new List<string> { $"docker exec -u 0 {PathText.ShellQuote(cfg.ContainerName)} touch -- {PathText.ShellQuote(Path.Combine(folder, ".ignore"))}" },
                    Description = $"Write an empty .ignore file in {folder} so Jellyfin's next scan skips this folder."
                });
            }
            else
            {
                plan.Warnings.Add($"Files at {where} stay on disk, so the next library scan will add this copy back unless you also write a .ignore file or move the files.");
            }
        }

        plan.BytesFreed = plan.Steps.Where(s => s.Kind is "ArrDelete" or "JellyfinDeleteFiles").Sum(s => s.Bytes);
        plan.Destructive = plan.Steps.Any(s => s.Destructive);
        plan.NeedsTypedDelete = plan.Destructive && cfg.ConfirmationLevel == ConfirmationLevel.TypeDelete;
        plan.CanExecute = plan.Steps.Count > 0 && plan.Steps.All(s => s.Possible);
        plan.Fingerprint = Fingerprint(plan);
        return plan;
    }

    private PlanStep? PlanUserData(ItemSnapshot loser, ItemSnapshot keeper, IReadOnlyList<UserRef> users)
    {
        var pairs = new List<(Guid From, Guid To)> { (loser.Id, keeper.Id) };
        if (loser.Kind == MediaKind.Series)
        {
            var keep = _library.GetEpisodes(keeper.Id, false).SelectMany(e => EpisodeOverlap.Keys(e).Take(1).Select(k => (Key: k, e.Id))).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
            foreach (var e in _library.GetEpisodes(loser.Id, false))
            {
                var key = EpisodeOverlap.Keys(e).FirstOrDefault();
                if (key is not null && keep.TryGetValue(key, out var target))
                {
                    pairs.Add((e.Id, target));
                }
            }
        }

        var step = new PlanStep { ItemId = loser.Id, Kind = "CopyUserData" };
        var perUser = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var u in users)
        {
            foreach (var (from, to) in pairs)
            {
                var src = _users.GetPlayState(u.Id, from);
                if (src is null || !src.HasAnyData)
                {
                    continue;
                }

                var dst = _users.GetPlayState(u.Id, to);
                if (dst is not null && dst.HasAnyData)
                {
                    continue;
                }

                step.UserDataCopies.Add((u.Id, from, to, src));
                perUser[u.Name] = perUser.GetValueOrDefault(u.Name) + 1;
            }
        }

        if (step.UserDataCopies.Count == 0)
        {
            return null;
        }

        step.Description = "Copy watch data (played, play count, favourite, resume position) to the kept copy where it has none: "
                           + string.Join(", ", perUser.Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key} ({kv.Value} item{(kv.Value == 1 ? string.Empty : "s")})"))) + ".";
        return step;
    }

    private async Task RunStepAsync(PlanStep step, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case "CopyUserData":
                foreach (var (userId, _, to, state) in step.UserDataCopies)
                {
                    _users.SavePlayState(userId, to, state);
                }

                step.ResultDetail = string.Create(CultureInfo.InvariantCulture, $"{step.UserDataCopies.Count} record(s) copied.");
                break;
            case "ArrDelete":
                await _arr.For(step.ArrKindOf).DeleteAsync(step.ArrId!.Value, true, ct).ConfigureAwait(false);
                break;
            case "JellyfinDeleteFiles":
                _library.RemoveItem(step.ItemId, true);
                var left = step.Paths.Where(Exists).ToList();
                if (left.Count > 0)
                {
                    throw new ToolkitException("delete_incomplete", "Jellyfin removed the item but these paths still exist: " + string.Join(", ", left), 500);
                }

                break;
            case "JellyfinRemove":
                _library.RemoveItem(step.ItemId, false);
                break;
            case "WriteIgnore":
                var path = step.Paths[0];
                if (!File.Exists(path))
                {
                    await File.WriteAllBytesAsync(path, Array.Empty<byte>(), ct).ConfigureAwait(false);
                }

                break;
            default:
                throw new ToolkitException("bad_step", "Unknown step " + step.Kind, 500);
        }
    }

    private void ForgetRemoved(string groupId, List<Guid> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var cache = GetCached();
        if (cache is null)
        {
            return;
        }

        foreach (var list in new[] { cache.Groups, cache.EpisodeGroups })
        {
            var g = list.FirstOrDefault(x => x.Id == groupId);
            if (g is null)
            {
                continue;
            }

            g.Copies.RemoveAll(c => removed.Contains(c.Item.Id));
            if (g.Copies.Count < 2)
            {
                list.Remove(g);
            }
        }

        _store.Write(CacheFile, cache);
        var choices = GetChoices();
        choices.Marked.Remove(groupId);
        choices.Keep.Remove(groupId);
        _store.Write(ChoicesFile, choices);
    }
}
