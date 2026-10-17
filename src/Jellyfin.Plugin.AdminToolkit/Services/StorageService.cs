using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Storage;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>One movie or series in the storage report.</summary>
public sealed class StorageItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string? Library { get; set; }

    public string? Drive { get; set; }

    public string? Path { get; set; }

    public long Bytes { get; set; }

    public int Episodes { get; set; }

    public DateTime DateAdded { get; set; }

    public DateTime? LastPlayed { get; set; }

    public string? LastPlayedBy { get; set; }

    /// <summary>Gets or sets the total plays by all users (episode plays for a series).</summary>
    public int PlayCount { get; set; }

    public string? DuplicateGroupId { get; set; }
}

public sealed class SizeBucket
{
    public string Name { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public int Items { get; set; }
}

public sealed class StorageReport
{
    public DateTime ScannedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public List<SizeBucket> Libraries { get; set; } = new();

    public List<SizeBucket> DrivesBySize { get; set; } = new();

    public List<StorageItem> Items { get; set; } = new();
}

/// <summary>Drive space, sizes per library/drive, largest titles and what nobody has watched.</summary>
public sealed class StorageService : IDisposable
{
    public const string CacheFile = "storage.json";

    private readonly ILibraryAdapter _library;
    private readonly IUserAdapter _users;
    private readonly IDriveSpaceProvider _space;
    private readonly JsonStore _store;
    private readonly ActivityLog _log;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<DuplicateScanResult?> _duplicates;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public StorageService(ILibraryAdapter library, IUserAdapter users, IDriveSpaceProvider space, JsonStore store, ActivityLog log, Func<PluginConfiguration> config, Func<DuplicateScanResult?> duplicates)
    {
        _library = library;
        _users = users;
        _space = space;
        _store = store;
        _log = log;
        _config = config;
        _duplicates = duplicates;
    }

    public bool IsScanning => _scanLock.CurrentCount == 0;

    public void Dispose() => _scanLock.Dispose();

    public StorageReport? GetCached() => _store.Read<StorageReport>(CacheFile);

    /// <summary>Live drive overview (cheap; no scan needed): configured drives plus unlabelled library folders.</summary>
    public List<DriveView> Drives()
    {
        var cfg = _config();
        var mapper = new DriveMapper(cfg.DriveMappings);
        var list = new List<DriveView>();
        foreach (var m in cfg.DriveMappings)
        {
            list.Add(View(m.Label, m.Prefix, true));
        }

        foreach (var loc in _library.GetLibraries().SelectMany(l => l.Locations).Distinct(StringComparer.Ordinal))
        {
            if (mapper.Find(loc) is null)
            {
                list.Add(View("(no label) " + loc, loc, false));
            }
        }

        return list;
    }

    public async Task<StorageReport> ScanAsync(string user, IProgress<double>? progress, CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new ToolkitException("busy", "A storage scan is already running.", 409);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var report = await Task.Run(() => Scan(progress, ct), ct).ConfigureAwait(false);
            report.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            _store.Write(CacheFile, report);
            _log.Add(user, "Storage", "Scan", "Library", "OK", string.Create(CultureInfo.InvariantCulture, $"{report.Items.Count} titles, {report.Items.Sum(i => i.Bytes) / 1e12:0.00} TB in {report.DurationSeconds}s."));
            return report;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Titles nobody has played in <paramref name="months"/> months (or ever), largest first.</summary>
    public List<StorageItem> NotWatched(int months, DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.UtcNow).AddMonths(-Math.Max(1, months));
        return (GetCached()?.Items ?? new List<StorageItem>())
            .Where(i => (i.LastPlayed is null || i.LastPlayed < cutoff) && i.DateAdded < cutoff)
            .OrderByDescending(i => i.Bytes)
            .ToList();
    }

    public string ExportCsv(IEnumerable<StorageItem> items)
        => Csv.Write(
            new[] { "itemId", "kind", "title", "year", "library", "drive", "path", "sizeBytes", "sizeGB", "episodes", "dateAdded", "lastPlayed", "lastPlayedBy", "playCount" },
            items.Select(i => new object?[] { i.Id.ToString("N", CultureInfo.InvariantCulture), i.Kind, i.Name, i.Year, i.Library, i.Drive, i.Path, i.Bytes, Math.Round(i.Bytes / 1e9, 2), i.Episodes, i.DateAdded, i.LastPlayed, i.LastPlayedBy, i.PlayCount }));

    private static long FileSize(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private DriveView View(string label, string prefix, bool configured)
    {
        var space = _space.GetSpace(prefix);
        bool exists = Directory.Exists(prefix);
        bool empty = false;
        if (exists)
        {
            try
            {
                empty = !Directory.EnumerateFileSystemEntries(prefix).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new DriveView
        {
            Label = label,
            Prefix = prefix,
            Configured = configured,
            Exists = exists,
            TotalBytes = space?.TotalBytes,
            FreeBytes = space?.FreeBytes,
            UsedBytes = space?.UsedBytes,
            Problem = !exists ? "Folder not found inside the Jellyfin container: drive not mounted?" : empty ? "Folder is empty: if this is a drive mount point, the drive is probably not mounted." : space is null ? "Could not read the drive's space." : null
        };
    }

    private StorageReport Scan(IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = _config();
        var mapper = new DriveMapper(cfg.DriveMappings);
        var users = _users.GetUsers();
        var dupByItem = new Dictionary<Guid, string>();
        foreach (var g in _duplicates()?.Groups ?? new List<DuplicateGroup>())
        {
            foreach (var c in g.Copies)
            {
                dupByItem[c.Item.Id] = g.Id;
            }
        }

        var titles = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Movie, MediaKind.Series }, WithStreams = false });
        var episodes = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Episode }, WithStreams = false })
            .Where(e => e.SeriesId is not null)
            .GroupBy(e => e.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var report = new StorageReport { ScannedUtc = DateTime.UtcNow };
        int done = 0;
        foreach (var t in titles)
        {
            ct.ThrowIfCancellationRequested();
            var si = new StorageItem
            {
                Id = t.Id,
                Name = t.Name,
                Year = t.Year,
                Kind = t.Kind,
                Library = t.LibraryName,
                Path = t.Kind == MediaKind.Series ? t.Path : t.FolderPath ?? t.Path,
                Drive = mapper.LabelFor(t.FolderPath ?? t.Path),
                DateAdded = t.DateCreated,
                DuplicateGroupId = dupByItem.GetValueOrDefault(t.Id)
            };
            var played = t.Kind == MediaKind.Series ? episodes.GetValueOrDefault(t.Id) ?? new List<ItemSnapshot>() : new List<ItemSnapshot> { t };
            if (t.Kind == MediaKind.Series)
            {
                si.Episodes = played.Count;
                si.Bytes = played.Sum(e => e.SizeBytes is > 0 ? e.SizeBytes.Value : FileSize(e.Path));
            }
            else
            {
                si.Bytes = t.FilePaths.Count > 0 ? t.FilePaths.Sum(FileSize) : 0;
                if (si.Bytes == 0)
                {
                    si.Bytes = t.SizeBytes ?? 0;
                }
            }

            foreach (var u in users)
            {
                foreach (var e in played)
                {
                    var st = _users.GetPlayState(u.Id, e.Id);
                    if (st is null)
                    {
                        continue;
                    }

                    si.PlayCount += st.PlayCount > 0 ? st.PlayCount : st.Played ? 1 : 0;
                    if (st.LastPlayedDate is not null && (si.LastPlayed is null || st.LastPlayedDate > si.LastPlayed))
                    {
                        si.LastPlayed = st.LastPlayedDate;
                        si.LastPlayedBy = u.Name;
                    }
                }
            }

            report.Items.Add(si);
            if (++done % 50 == 0)
            {
                progress?.Report(100.0 * done / Math.Max(1, titles.Count));
            }
        }

        report.Items = report.Items.OrderByDescending(i => i.Bytes).ToList();
        report.Libraries = report.Items.GroupBy(i => i.Library ?? "(unknown)").Select(g => new SizeBucket { Name = g.Key, Bytes = g.Sum(i => i.Bytes), Items = g.Count() }).OrderByDescending(b => b.Bytes).ToList();
        report.DrivesBySize = report.Items.GroupBy(i => i.Drive ?? "(no label)").Select(g => new SizeBucket { Name = g.Key, Bytes = g.Sum(i => i.Bytes), Items = g.Count() }).OrderByDescending(b => b.Bytes).ToList();
        progress?.Report(100);
        return report;
    }
}
