using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public sealed class HealthStorageTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeLibrary _lib = new();
    private readonly FakeUsers _users = new();
    private readonly PluginConfiguration _cfg = new();
    private readonly JsonStore _store;
    private readonly ActivityLog _log;
    private readonly HealthService _health;
    private readonly StorageService _storage;
    private readonly string _tv;
    private readonly string _movies;
    private readonly string _unmounted;

    public HealthStorageTests()
    {
        _tv = Directory.CreateDirectory(Path.Combine(_tmp.Path, "8tb2", "TV")).FullName;
        _movies = Directory.CreateDirectory(Path.Combine(_tmp.Path, "1tb", "Movies")).FullName;
        _unmounted = Directory.CreateDirectory(Path.Combine(_tmp.Path, "8tb1")).FullName;
        _cfg.DriveMappings.Add(new DriveMapping { Prefix = Path.Combine(_tmp.Path, "8tb2"), Label = "8TB 2" });
        _cfg.DriveMappings.Add(new DriveMapping { Prefix = Path.Combine(_tmp.Path, "1tb"), Label = "1TB" });
        _cfg.DriveMappings.Add(new DriveMapping { Prefix = _unmounted, Label = "8TB 1" });
        _lib.Libraries.Add(new LibraryInfo { Id = Guid.NewGuid(), Name = "TV", Locations = { _tv, _unmounted } });
        _lib.Libraries.Add(new LibraryInfo { Id = Guid.NewGuid(), Name = "Movies", Locations = { _movies } });
        _store = new JsonStore(Path.Combine(_tmp.Path, "data"));
        _log = new ActivityLog(_store.Directory, () => 30);
        _health = new HealthService(_lib, _store, _log, new SafetyGuard(() => _cfg), () => _cfg);
        _storage = new StorageService(_lib, _users, new FakeSpace(), _store, _log, () => _cfg, () => null);
    }

    public void Dispose()
    {
        _health.Dispose();
        _storage.Dispose();
        _tmp.Dispose();
    }

    private ItemSnapshot Movie(string folderName, bool createFile = true, int height = 1080)
    {
        var folder = Path.Combine(_movies, folderName);
        var file = Path.Combine(folder, folderName + ".mkv");
        if (createFile)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(file, new byte[2048]);
        }

        var m = new ItemSnapshot { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = folderName, Path = file, FolderPath = folder, FilePaths = { file }, Height = height, Width = height * 16 / 9, VideoCodec = "h264", AudioLanguages = { "eng" }, SubtitleLanguages = { "eng" }, HasOverview = true, Year = 2000, LibraryName = "Movies", DateCreated = new DateTime(2020, 1, 1) };
        m.ProviderIds["Tmdb"] = "1";
        m.ImageCounts["Primary"] = 1;
        m.ImageCounts["Backdrop"] = 1;
        m.ImageCounts["Logo"] = 1;
        _lib.Items.Add(m);
        return m;
    }

    [Fact]
    public async Task Health_FindsUnmountedDrive_GhostsGapsNamingAndQuality()
    {
        Movie("Good Film (2000)");
        var ghost = Movie("Gone Film (2001)", createFile: false);
        var noYear = Movie("No Year Film");
        noYear.ProviderIds.Clear();
        noYear.SubtitleLanguages.Clear();
        noYear.VideoCodec = "hevc";
        noYear.BitDepth = 10;
        var sd = Movie("Old Film (1960)", height: 480);
        sd.ImageCounts.Remove("Logo");
        var show = _lib.AddSeries("Show", 2010, Path.Combine(_tv, "Show"), 10, 24, true, ("Tvdb", "5"));
        _lib.Items.RemoveAll(i => i.SeriesId == show.Id && i.SeasonNumber == 1 && i.EpisodeNumber == 4);
        File.WriteAllText(Path.Combine(_tv, "Show", "Season 1", "movie.part"), "x");
        Directory.CreateDirectory(Path.Combine(_tv, "Empty Folder"));

        var r = await _health.ScanAsync("admin", null, CancellationToken.None);

        var unmounted = Assert.Single(r.LibraryPaths, p => p.Path == _unmounted);
        Assert.True(unmounted.Empty);
        Assert.Contains("probably not mounted", unmounted.Status, StringComparison.Ordinal);
        Assert.Equal("8TB 1", unmounted.Drive);
        Assert.Contains(r.Issues, i => i.Category == "UnreachablePath" && i.Path == _unmounted);
        Assert.True(Assert.Single(r.LibraryPaths, p => p.Path == _movies).Writable);

        Assert.Contains(r.Issues, i => i.Category == "GhostItem" && i.ItemId == ghost.Id);
        Assert.Contains(r.Issues, i => i.Category == "NoProviderIds" && i.ItemId == noYear.Id);
        Assert.Contains(r.Issues, i => i.Category == "FolderNaming" && i.ItemId == noYear.Id && i.Detail.Contains("no year", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "SubtitleAudit" && i.ItemId == noYear.Id);
        Assert.Contains(r.Issues, i => i.Category == "DirectPlay" && i.ItemId == noYear.Id);
        Assert.Contains(r.Issues, i => i.Category == "QualityAudit" && i.ItemId == sd.Id && i.Detail.Contains("low resolution", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "MissingImage" && i.ItemId == sd.Id && i.Detail == "No logo image.");
        Assert.Contains(r.Issues, i => i.Category == "FewEpisodes" && i.ItemId == show.Id && i.Detail.StartsWith("Only 9 of 23", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "EpisodeGap" && i.ItemId == show.Id && i.Detail == "Season 1: missing episode(s) 4.");
        Assert.Contains(r.Issues, i => i.Category == "LeftoverFile" && i.Path!.EndsWith("movie.part", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "EmptyFolder" && i.Path!.EndsWith("Empty Folder", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(_tv, "Empty Folder")));
        Assert.NotNull(_health.GetCached());
    }

    [Fact]
    public void Health_Helpers()
    {
        Assert.Equal(new[] { 3, 5, 6 }, HealthService.MissingNumbers(new[] { 1, 2, 4, 7 }));
        Assert.Equal("3, 5-6, 9", HealthService.Ranges(new[] { 3, 5, 6, 9 }));
        Assert.True(HealthService.IsSeasonFolderName("Season 01"));
        Assert.True(HealthService.IsSeasonFolderName("Specials"));
        Assert.True(HealthService.IsSeasonFolderName("S2"));
        Assert.False(HealthService.IsSeasonFolderName("Disc 1"));
        Assert.NotNull(HealthService.NameProblem("Film: The Sequel"));
        Assert.NotNull(HealthService.NameProblem("Film. "));
        Assert.Null(HealthService.NameProblem("Film (2001)"));
    }

    [Fact]
    public void Refresh_IsRefusedInSafeMode_ThenQueued()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ToolkitException>(() => _health.Refresh(new[] { id }, false, "admin"));
        _cfg.SafeMode = false;
        _health.Refresh(new[] { id }, false, "admin");
        _health.RescanLibrary(_lib.Libraries[0].Id, "admin");
        Assert.Equal(new[] { (id, false), (_lib.Libraries[0].Id, false) }, _lib.Refreshed);
    }

    [Fact]
    public async Task Storage_SizesDrivesAndNotWatched()
    {
        var watched = Movie("Watched (2000)");
        var never = Movie("Never (2000)");
        var show = _lib.AddSeries("Show", 2010, Path.Combine(_tv, "Show"), 3, 3, true);
        var alex = Guid.NewGuid();
        _users.Users.Add(new UserRef(alex, "alex", false, false));
        _users.Data[(alex, watched.Id)] = new PlayState { Played = true, PlayCount = 2, LastPlayedDate = DateTime.UtcNow.AddDays(-3) };

        var drives = _storage.Drives();
        Assert.Contains(drives, d => d.Label == "8TB 1" && d.Problem!.Contains("not mounted", StringComparison.Ordinal));
        Assert.Contains(drives, d => d.Label == "1TB" && d.FreeBytes == 2_000_000_000_000);

        var r = await _storage.ScanAsync("admin", null, CancellationToken.None);
        var s = r.Items.Single(i => i.Id == show.Id);
        Assert.Equal(3000, s.Bytes);
        Assert.Equal(3, s.Episodes);
        Assert.Equal("8TB 2", s.Drive);
        var w = r.Items.Single(i => i.Id == watched.Id);
        Assert.Equal(2, w.PlayCount);
        Assert.Equal("alex", w.LastPlayedBy);
        Assert.Equal("1TB", w.Drive);
        Assert.Contains(r.DrivesBySize, b => b.Name == "1TB" && b.Items == 2);

        var notWatched = _storage.NotWatched(6);
        Assert.Contains(notWatched, i => i.Id == never.Id);
        Assert.DoesNotContain(notWatched, i => i.Id == watched.Id);
        Assert.Contains("Never (2000)", _storage.ExportCsv(notWatched), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduledTask_DoesNothingUntilAllowed_AndHasNoDefaultTrigger()
    {
        using var dup = new DuplicateService(_lib, _users, new FakeSpace(), new ArrFactory(() => new HttpClient(), () => _cfg), _store, _log, new SafetyGuard(() => _cfg), () => _cfg);
        var task = new ScanTask(dup, _health, _log, () => _cfg, NullLogger<ScanTask>.Instance);
        Assert.Empty(task.GetDefaultTriggers());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.Null(dup.GetCached());
        Assert.Null(_health.GetCached());
        Assert.Contains(_log.Read(new ActivityFilter()), e => e.Result == "Refused");

        _cfg.ScheduledScansEnabled = true;
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        Assert.NotNull(dup.GetCached());
        Assert.NotNull(_health.GetCached());
    }
}
