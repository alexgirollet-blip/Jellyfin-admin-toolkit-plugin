using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Storage;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>One problem found by the health scan.</summary>
public sealed class HealthIssue
{
    /// <summary>Gets or sets the category (GhostItem, UnreachablePath, NoProviderIds, FewEpisodes, MissingImage, ...).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets Error, Warning or Info.</summary>
    public string Severity { get; set; } = "Info";

    public Guid? ItemId { get; set; }

    public MediaKind? Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? Library { get; set; }

    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the actions the UI offers: refresh, open, images, rescanLibrary.</summary>
    public List<string> Actions { get; set; } = new();
}

/// <summary>State of one library folder.</summary>
public sealed class LibraryPathStatus
{
    public Guid LibraryId { get; set; }

    public string Library { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? Drive { get; set; }

    public bool Exists { get; set; }

    public bool Readable { get; set; }

    public bool Empty { get; set; }

    public bool Writable { get; set; }

    public string? WriteProblem { get; set; }

    public string Status { get; set; } = string.Empty;
}

public sealed class HealthReport
{
    public DateTime ScannedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public int ItemsScanned { get; set; }

    public List<LibraryPathStatus> LibraryPaths { get; set; } = new();

    public List<HealthIssue> Issues { get; set; } = new();

    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);

    public string ProcessUser { get; set; } = string.Empty;
}

/// <summary>Library health checks. Read-only; the only actions are Jellyfin metadata refreshes and library rescans.</summary>
public sealed partial class HealthService : IDisposable
{
    public const string CacheFile = "health.json";
    private const int MaxWalkEntries = 200_000;

    private static readonly HashSet<string> OddCodecs = new(StringComparer.OrdinalIgnoreCase) { "mpeg2video", "mpeg1video", "mpeg4", "msmpeg4v3", "msmpeg4v2", "vc1", "wmv3", "wmv2", "mjpeg", "h263", "theora", "rv40", "vp8" };
    private static readonly HashSet<string> LeftoverExtensions = new(StringComparer.OrdinalIgnoreCase) { ".part", ".!qb", ".crdownload", ".tmp", ".exe", ".bat", ".scr", ".lnk", ".url", ".torrent", ".rar", ".r00", ".zip", ".7z", ".parts", ".!ut", ".aria2" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv", ".mpg", ".mpeg", ".webm", ".m2ts", ".iso" };

    private readonly ILibraryAdapter _library;
    private readonly JsonStore _store;
    private readonly ActivityLog _log;
    private readonly SafetyGuard _guard;
    private readonly Func<PluginConfiguration> _config;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public HealthService(ILibraryAdapter library, JsonStore store, ActivityLog log, SafetyGuard guard, Func<PluginConfiguration> config)
    {
        _library = library;
        _store = store;
        _log = log;
        _guard = guard;
        _config = config;
    }

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public void Dispose() => _scanLock.Dispose();

    public HealthReport? GetCached() => _store.Read<HealthReport>(CacheFile);

    public async Task<HealthReport> ScanAsync(string user, IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new ToolkitException("busy", "A health scan is already running.", 409);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var report = await Task.Run(() => Scan(progress, ct), ct).ConfigureAwait(false);
            report.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            _store.Write(CacheFile, report);
            _log.Add(user, "Health", "Scan", "Library", "OK", string.Create(CultureInfo.InvariantCulture, $"{report.ItemsScanned} items, {report.Issues.Count} findings in {report.DurationSeconds}s."));
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolkitException)
        {
            _log.Add(user, "Health", "Scan", "Library", "Failed", ErrorText.Describe(ex));
            throw;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Queues Jellyfin metadata refreshes ("Refresh metadata" in the dashboard) for the selected items.</summary>
    public int Refresh(IReadOnlyList<Guid> itemIds, bool replaceAll, string user)
    {
        _guard.EnsureChangesAllowed("refresh metadata");
        foreach (var id in itemIds.Distinct().Take(5000))
        {
            _library.QueueRefresh(id, replaceAll);
        }

        _log.Add(user, "Health", replaceAll ? "Replace all metadata" : "Refresh metadata", string.Create(CultureInfo.InvariantCulture, $"{itemIds.Count} item(s)"), "OK", "Queued in Jellyfin's refresh queue.");
        return itemIds.Count;
    }

    /// <summary>Queues a scan of one library (the same as "Scan library" for that library in the dashboard).</summary>
    public void RescanLibrary(Guid libraryId, string user)
    {
        _guard.EnsureChangesAllowed("rescan a library");
        var lib = _library.GetLibraries().FirstOrDefault(l => l.Id == libraryId) ?? throw new ToolkitException("not_found", "Library not found.", 404);
        _library.QueueRefresh(libraryId, false);
        _log.Add(user, "Health", "Rescan library", lib.Name, "OK", "Queued in Jellyfin's refresh queue.");
    }

    internal static List<int> MissingNumbers(IEnumerable<int> present)
    {
        var set = new HashSet<int>(present.Where(n => n > 0));
        if (set.Count == 0)
        {
            return new List<int>();
        }

        return Enumerable.Range(1, set.Max()).Where(n => !set.Contains(n)).ToList();
    }

    internal static string Ranges(IReadOnlyList<int> numbers)
    {
        var parts = new List<string>();
        for (int i = 0; i < numbers.Count; i++)
        {
            int start = numbers[i];
            while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1)
            {
                i++;
            }

            parts.Add(start == numbers[i] ? start.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{start}-{numbers[i]}"));
        }

        return string.Join(", ", parts);
    }

    internal static string? NameProblem(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (OddChars().IsMatch(name))
        {
            return "contains characters that some file systems and clients reject (< > : \" | ? * or control characters)";
        }

        if (name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith(' '))
        {
            return "starts or ends with a space or ends with a dot";
        }

        if (name.Contains("  ", StringComparison.Ordinal))
        {
            return "contains double spaces";
        }

        return null;
    }

    internal static bool IsSeasonFolderName(string name) => SeasonFolder().IsMatch(name);

    private static HealthIssue Issue(string category, string severity, ItemSnapshot? item, string detail, params string[] actions)
        => new()
        {
            Category = category,
            Severity = severity,
            ItemId = item?.Id,
            Kind = item?.Kind,
            Title = item is null ? string.Empty : item.Kind == MediaKind.Episode ? $"{item.SeriesName} S{item.SeasonNumber:00}E{item.EpisodeNumber:00}" : item.Year is null ? item.Name : $"{item.Name} ({item.Year})",
            Path = item?.Path,
            Library = item?.LibraryName,
            Detail = detail,
            Actions = actions.ToList()
        };

    private HealthReport Scan(IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = _config();
        var drives = new DriveMapper(cfg.DriveMappings);
        var report = new HealthReport { ScannedUtc = DateTime.UtcNow, ProcessUser = PermissionProbe.DescribeProcessUser() };

        var unreachable = new List<string>();
        foreach (var lib in _library.GetLibraries())
        {
            foreach (var loc in lib.Locations)
            {
                var st = new LibraryPathStatus { LibraryId = lib.Id, Library = lib.Name, Path = loc, Drive = drives.LabelFor(loc), Exists = Directory.Exists(loc) };
                if (st.Exists)
                {
                    st.Readable = PermissionProbe.CheckReadable(loc).Ok;
                    try
                    {
                        st.Empty = !Directory.EnumerateFileSystemEntries(loc).Any();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        st.Readable = false;
                    }

                    var w = PermissionProbe.CheckWritableDirectory(loc);
                    st.Writable = w.Ok;
                    st.WriteProblem = w.Ok ? null : w.Message;
                }

                st.Status = !st.Exists ? "Not found: the folder does not exist inside the Jellyfin container (drive not mounted?)."
                    : !st.Readable ? "Exists but Jellyfin cannot read it."
                    : st.Empty ? "Empty: if this is a drive mount point, the drive is probably not mounted."
                    : st.Writable ? "OK (readable and writable by Jellyfin)." : "OK for playback (readable). Not writable by Jellyfin, so files here cannot be deleted from Jellyfin.";
                report.LibraryPaths.Add(st);
                if (!st.Exists || !st.Readable || st.Empty)
                {
                    unreachable.Add(loc);
                    report.Issues.Add(new HealthIssue { Category = "UnreachablePath", Severity = "Error", Title = lib.Name, Path = loc, Library = lib.Name, Detail = st.Status, Actions = { "rescanLibrary" }, ItemId = lib.Id });
                }
            }
        }

        progress?.Report(5);
        var items = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Movie, MediaKind.Series, MediaKind.Episode }, WithStreams = true, IncludeVirtual = true });
        var real = items.Where(i => !i.IsVirtual).ToList();
        report.ItemsScanned = real.Count;
        progress?.Report(35);

        var ghostsUnderUnreachable = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in real)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(i.Path))
            {
                continue;
            }

            bool exists = i.Kind == MediaKind.Series ? Directory.Exists(i.Path) : File.Exists(i.Path) || Directory.Exists(i.Path);
            if (!exists)
            {
                var root = unreachable.FirstOrDefault(u => PathText.IsUnder(i.Path, u));
                if (root is not null)
                {
                    ghostsUnderUnreachable[root] = ghostsUnderUnreachable.GetValueOrDefault(root) + 1;
                }
                else
                {
                    report.Issues.Add(Issue("GhostItem", "Error", i, "The file or folder no longer exists on disk. A library scan removes it; or the file was moved.", "open", "rescanLibrary"));
                }
            }
        }

        foreach (var (root, count) in ghostsUnderUnreachable)
        {
            report.Issues.Add(new HealthIssue { Category = "GhostItem", Severity = "Error", Title = root, Path = root, Detail = string.Create(CultureInfo.InvariantCulture, $"{count} item(s) under this unreachable library folder are missing on disk. Fix the mount before scanning the library, or Jellyfin will remove them (and their watch history).") });
        }

        foreach (var i in real.Where(i => i.Kind is MediaKind.Movie or MediaKind.Series))
        {
            if (!i.HasProviderIds)
            {
                report.Issues.Add(Issue("NoProviderIds", "Warning", i, "No TMDb/TVDb/IMDb id: the item is probably unmatched or wrongly identified. Use Identify in Jellyfin.", "open", "refresh"));
            }

            foreach (var type in new[] { "Primary", "Backdrop", "Logo" })
            {
                if (i.Images(type) == 0)
                {
                    report.Issues.Add(Issue("MissingImage", type == "Primary" ? "Warning" : "Info", i, $"No {(type == "Primary" ? "poster" : type.ToLowerInvariant())} image.", "images", "refresh"));
                }
            }

            if (!i.HasOverview)
            {
                report.Issues.Add(Issue("MissingOverview", "Info", i, "No overview (plot summary).", "refresh", "open"));
            }

            if (i.Year is null)
            {
                report.Issues.Add(Issue("MissingYear", "Info", i, "No production year.", "refresh", "open"));
            }

            var folder = Path.GetFileName(PathText.Normalize(i.Kind == MediaKind.Movie ? i.FolderPath : i.Path));
            if (i.Kind == MediaKind.Movie && !string.IsNullOrEmpty(folder) && !YearInName().IsMatch(folder) && !unreachable.Any(u => PathText.SamePath(u, i.FolderPath)))
            {
                report.Issues.Add(Issue("FolderNaming", "Info", i, $"Folder \"{folder}\" has no year; Jellyfin matches more reliably with \"Title (Year)\".", "open"));
            }

            var problem = NameProblem(folder ?? string.Empty);
            if (problem is not null)
            {
                report.Issues.Add(Issue("FolderNaming", "Warning", i, $"Folder \"{folder}\" {problem}.", "open"));
            }
        }

        progress?.Report(55);
        var episodesBySeries = items.Where(i => i.Kind == MediaKind.Episode && i.SeriesId is not null).GroupBy(i => i.SeriesId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var s in real.Where(i => i.Kind == MediaKind.Series))
        {
            ct.ThrowIfCancellationRequested();
            var eps = episodesBySeries.GetValueOrDefault(s.Id) ?? new List<ItemSnapshot>();
            var present = eps.Where(e => !e.IsVirtual && e.SeasonNumber is > 0).ToList();
            int missingVirtual = eps.Count(e => e.IsVirtual && e.SeasonNumber is > 0);
            if (present.Count == 0)
            {
                report.Issues.Add(Issue("FewEpisodes", "Warning", s, "The series has no episode files.", "open", "refresh"));
                continue;
            }

            if (missingVirtual > 0 && present.Count < 0.5 * (present.Count + missingVirtual))
            {
                report.Issues.Add(Issue("FewEpisodes", "Warning", s, string.Create(CultureInfo.InvariantCulture, $"Only {present.Count} of {present.Count + missingVirtual} episodes known to the metadata provider are present."), "open", "refresh"));
            }

            var seasons = present.Select(e => e.SeasonNumber!.Value).Distinct().ToList();
            var missingSeasons = MissingNumbers(seasons);
            if (missingSeasons.Count > 0)
            {
                report.Issues.Add(Issue("SeasonGap", "Info", s, "Missing season(s): " + Ranges(missingSeasons) + ".", "open"));
            }

            foreach (var season in present.GroupBy(e => e.SeasonNumber!.Value).OrderBy(g => g.Key))
            {
                var nums = season.SelectMany(e => Enumerable.Range(e.EpisodeNumber ?? 0, Math.Max(1, Math.Min((e.EpisodeNumberEnd ?? e.EpisodeNumber ?? 0) - (e.EpisodeNumber ?? 0) + 1, 50))));
                var gaps = MissingNumbers(nums);
                if (gaps.Count > 0)
                {
                    report.Issues.Add(Issue("EpisodeGap", "Info", s, string.Create(CultureInfo.InvariantCulture, $"Season {season.Key}: missing episode(s) {Ranges(gaps)}."), "open"));
                }
            }

            var seasonFolders = present
                .Select(e => e.FolderPath)
                .Where(f => f is not null && !PathText.SamePath(f, s.Path) && PathText.IsUnder(f, s.Path))
                .Select(f => Path.GetFileName(PathText.Normalize(f)))
                .Distinct(StringComparer.Ordinal)
                .Where(n => !IsSeasonFolderName(n))
                .ToList();
            if (seasonFolders.Count > 0)
            {
                report.Issues.Add(Issue("FolderNaming", "Info", s, $"Season folder name(s) not in Jellyfin's \"Season 01\" / \"Specials\" form: {string.Join(", ", seasonFolders.Take(5).Select(n => "\"" + n + "\""))}.", "open"));
            }

            AuditVideos(report, s, present, cfg);
        }

        foreach (var m in real.Where(i => i.Kind == MediaKind.Movie))
        {
            AuditVideos(report, m, new List<ItemSnapshot> { m }, cfg);
        }

        progress?.Report(75);
        foreach (var st in report.LibraryPaths.Where(p => p.Exists && p.Readable && !p.Empty))
        {
            ct.ThrowIfCancellationRequested();
            WalkForLeftovers(report, st);
        }

        report.Counts = report.Issues.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        progress?.Report(100);
        return report;
    }

    private static void AuditVideos(HealthReport report, ItemSnapshot owner, List<ItemSnapshot> videos, PluginConfiguration cfg)
    {
        int n = videos.Count;
        string Scope(int count) => owner.Kind == MediaKind.Series ? string.Create(CultureInfo.InvariantCulture, $"{count} of {n} episode(s)") : "This movie";
        bool probed(ItemSnapshot v) => v.AudioLanguages.Count > 0 || v.VideoCodec is not null;
        var probedVideos = videos.Where(probed).ToList();
        if (probedVideos.Count == 0)
        {
            return;
        }

        int noSubs = probedVideos.Count(v => !v.SubtitleLanguages.Contains(cfg.AuditSubtitleLanguage, StringComparer.OrdinalIgnoreCase));
        if (noSubs > 0)
        {
            report.Issues.Add(Issue("SubtitleAudit", "Info", owner, $"{Scope(noSubs)}: no \"{cfg.AuditSubtitleLanguage}\" subtitles.", "open"));
        }

        int noAudio = probedVideos.Count(v => v.AudioLanguages.Count > 0 && !v.AudioLanguages.Contains(cfg.AuditAudioLanguage, StringComparer.OrdinalIgnoreCase));
        if (noAudio > 0)
        {
            report.Issues.Add(Issue("AudioAudit", "Info", owner, $"{Scope(noAudio)}: no \"{cfg.AuditAudioLanguage}\" audio track (\"und\" means the language is not tagged).", "open"));
        }

        int lowRes = probedVideos.Count(v => v.Height is > 0 and < 700 && (v.Width ?? 0) < 1200);
        if (lowRes > 0)
        {
            report.Issues.Add(Issue("QualityAudit", "Info", owner, $"{Scope(lowRes)}: low resolution (SD).", "open"));
        }

        var odd = probedVideos.Where(v => v.VideoCodec is not null && OddCodecs.Contains(v.VideoCodec)).Select(v => v.VideoCodec!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (odd.Count > 0)
        {
            report.Issues.Add(Issue("QualityAudit", "Info", owner, $"Old or unusual video codec ({string.Join(", ", odd)}): most clients need transcoding.", "open"));
        }

        int high = probedVideos.Count(v => v.Bitrate is > 0 && v.Bitrate > cfg.HighBitrateMbps * 1_000_000L);
        if (high > 0)
        {
            report.Issues.Add(Issue("QualityAudit", "Info", owner, string.Create(CultureInfo.InvariantCulture, $"{Scope(high)}: bitrate above {cfg.HighBitrateMbps} Mbps; may buffer on Wi-Fi or remote streams."), "open"));
        }

        int hard = probedVideos.Count(v => (string.Equals(v.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase) && v.BitDepth >= 10)
                                           || (v.VideoRange is not null && !v.VideoRange.Equals("SDR", StringComparison.OrdinalIgnoreCase) && !v.VideoRange.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                                           || string.Equals(v.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase));
        if (hard > 0)
        {
            report.Issues.Add(Issue("DirectPlay", "Info", owner, $"{Scope(hard)}: HEVC 10-bit, HDR/Dolby Vision or AV1. Informational: likely to need transcoding (or tone mapping) on web browsers, older TVs and some phones.", "open"));
        }
    }

    private static void WalkForLeftovers(HealthReport report, LibraryPathStatus root)
    {
        int seen = 0;
        int empties = 0;
        int leftovers = 0;
        var stack = new Stack<string>();
        stack.Push(root.Path);
        while (stack.Count > 0 && seen < MaxWalkEntries)
        {
            var dir = stack.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            seen += entries.Count;
            if (entries.Count == 0 && !PathText.SamePath(dir, root.Path))
            {
                if (empties++ < 200)
                {
                    report.Issues.Add(new HealthIssue { Category = "EmptyFolder", Severity = "Info", Title = Path.GetFileName(dir), Path = dir, Library = root.Library, Detail = "Empty folder (report only; nothing is deleted)." });
                }

                continue;
            }

            foreach (var e in entries)
            {
                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(e);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if ((attrs & FileAttributes.ReparsePoint) == 0)
                    {
                        stack.Push(e);
                    }

                    continue;
                }

                var ext = Path.GetExtension(e);
                var name = Path.GetFileName(e);
                string? why = LeftoverExtensions.Contains(ext) ? $"leftover download/archive file ({ext})" : null;
                if (why is null && VideoExtensions.Contains(ext) && SampleName().IsMatch(name))
                {
                    try
                    {
                        if (new FileInfo(e).Length < 300L * 1024 * 1024)
                        {
                            why = "looks like a sample clip";
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                if (why is not null && leftovers++ < 500)
                {
                    report.Issues.Add(new HealthIssue { Category = "LeftoverFile", Severity = "Info", Title = name, Path = e, Library = root.Library, Detail = char.ToUpperInvariant(why[0]) + why[1..] + " (report only; nothing is deleted)." });
                }
            }
        }

        if (seen >= MaxWalkEntries)
        {
            report.Issues.Add(new HealthIssue { Category = "LeftoverFile", Severity = "Info", Title = root.Path, Path = root.Path, Library = root.Library, Detail = string.Create(CultureInfo.InvariantCulture, $"Stopped after {MaxWalkEntries} entries; the rest of this folder was not checked for empty folders/leftovers.") });
        }
    }

    [GeneratedRegex(@"[<>:""|?*\x00-\x1F]", RegexOptions.CultureInvariant)]
    private static partial Regex OddChars();

    [GeneratedRegex(@"[\(\[]\s*(19|20)\d{2}\s*[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex YearInName();

    [GeneratedRegex(@"^(season|series|staffel|saison|temporada|stagione)[\s._-]*\d{1,4}$|^s\d{1,4}$|^specials?$|^extras?$|^featurettes$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(^|[\s._-])sample([\s._-]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SampleName();
}
