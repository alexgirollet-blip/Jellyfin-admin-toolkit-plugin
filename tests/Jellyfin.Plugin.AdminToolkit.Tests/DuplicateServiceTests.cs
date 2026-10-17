using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

/// <summary>
/// The user's real case: a show moved from "8TB 1" to "8TB 2", the library scanned both folders, Sonarr manages the
/// new path. Runs the real DuplicateService against fake Jellyfin adapters and real folders in a temp directory.
/// </summary>
public sealed class DuplicateServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeLibrary _lib = new();
    private readonly FakeUsers _users = new();
    private readonly PluginConfiguration _cfg;
    private readonly FakeHandler _sonarrHttp;
    private readonly DuplicateService _svc;
    private readonly ActivityLog _log;
    private readonly ItemSnapshot _stale;
    private readonly ItemSnapshot _current;
    private readonly Guid _alex = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public DuplicateServiceTests()
    {
        var d1 = Path.Combine(_tmp.Path, "8tb1");
        var d2 = Path.Combine(_tmp.Path, "8tb2");
        _cfg = new PluginConfiguration
        {
            SonarrUrl = "http://sonarr:8989",
            SonarrApiKey = "k",
            ArrPathMappings = { new PathMapping { ArrPrefix = "/tv", JellyfinPrefix = Path.Combine(d2, "TV") } },
            DriveMappings = { new DriveMapping { Prefix = d1, Label = "8TB 1" }, new DriveMapping { Prefix = d2, Label = "8TB 2" } }
        };
        _stale = _lib.AddSeries("Show", 2015, Path.Combine(d1, "TV", "Show"), 18, 24, true, ("Tvdb", "100"));
        _current = _lib.AddSeries("Show", 2015, Path.Combine(d2, "TV", "Show"), 24, 24, true, ("Tvdb", "100"));
        _lib.AddSeries("Other", 2001, Path.Combine(d2, "TV", "Other"), 3, 3, true, ("Tvdb", "200"));
        _users.Users.Add(new UserRef(_alex, "alex", false, false));
        var watched = _lib.GetEpisodes(_stale.Id, false).First(e => e.SeasonNumber == 1 && e.EpisodeNumber == 3);
        _users.Data[(_alex, watched.Id)] = new PlayState { Played = true, PlayCount = 2, LastPlayedDate = new DateTime(2024, 5, 1) };

        _sonarrHttp = new FakeHandler(r => r.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : FakeHandler.Json("""[{ "id": 42, "title": "Show", "year": 2015, "path": "/tv/Show", "tvdbId": 100 }]"""));
        var store = new JsonStore(Path.Combine(_tmp.Path, "data"));
        _log = new ActivityLog(store.Directory, () => 30);
        _svc = new DuplicateService(_lib, _users, new FakeSpace(), new ArrFactory(() => new HttpClient(_sonarrHttp), () => _cfg), store, _log, new SafetyGuard(() => _cfg), () => _cfg);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _tmp.Dispose();
    }

    [Fact]
    public async Task Scan_FindsTheMovedShow_AndRecommendsTheSonarrCopy()
    {
        var r = await _svc.ScanAsync("admin", null, CancellationToken.None);

        var g = Assert.Single(r.Groups);
        Assert.Equal(Confidence.High, g.Confidence);
        Assert.Equal(_current.Id, g.RecommendedKeepId);
        var keep = g.Copies.Single(c => c.Item.Id == _current.Id);
        var stale = g.Copies.Single(c => c.Item.Id == _stale.Id);
        Assert.Equal("8TB 2", keep.Drive);
        Assert.Equal("8TB 1", stale.Drive);
        Assert.True(keep.ArrManaged);
        Assert.False(stale.ArrManaged);
        Assert.Equal(24, keep.EpisodeCount);
        Assert.Equal(18, stale.EpisodeCount);
        Assert.Equal(24, stale.ExpectedEpisodes);
        Assert.Equal("Jellyfin's missing-episode data", stale.ExpectedSource);
        Assert.Equal(18, stale.Coverage!.SharedWithAll);
        Assert.Equal(0, stale.Coverage.OnlyHere);
        Assert.Equal(6, stale.Coverage.MissingHere);
        Assert.Equal(18000, stale.TotalSizeBytes);
        Assert.Equal(2_000_000_000_000, stale.DriveFreeBytes);
        Assert.Equal("alex", Assert.Single(stale.Watches).UserName);
        Assert.Contains(keep.Reasons, x => x.StartsWith("path matches Sonarr's path", StringComparison.Ordinal));
        Assert.Contains(stale.Warnings, w => w.Contains("alex", StringComparison.Ordinal));
        Assert.Contains(g.Evidence, e => e.StartsWith("Episode overlap: 18 episode(s) in every copy", StringComparison.Ordinal));
        Assert.NotNull(_svc.GetCached());
        Assert.Contains(_log.Read(new ActivityFilter()), e => e.Action == "Scan" && e.Result == "OK");
    }

    [Fact]
    public async Task MarkAndExport_ListsTheStaleCopy_AndDeletesNothing()
    {
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        _svc.Mark(new[] { g.Id }, true, "admin");
        var csv = _svc.ExportCsv(false);
        var row = Assert.Single(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1));
        Assert.Contains(_stale.Id.ToString("N") + "," + _stale.Path + ",8TB 1,", row, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_stale.Path));
        Assert.Empty(_lib.Removed);
        Assert.Single(_svc.RemovalList(true));
    }

    [Fact]
    public async Task Override_IsRemembered_AcrossRescans()
    {
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var swapped = _svc.SetKeep(g.Id, _stale.Id, "admin");
        Assert.True(swapped.Overridden);
        var again = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        Assert.Equal(_stale.Id, again.KeepId);
        Assert.Equal(_current.Id, again.RecommendedKeepId);
    }

    [Fact]
    public async Task RemoveUnmanagedCopy_PreviewThenConfirm_DeletesFilesAndCopiesWatchData()
    {
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _current.Id, RemoveItemIds = { _stale.Id }, DeleteFiles = true, CopyUserData = true, DryRun = false };

        var plan = await _svc.PlanAsync(req, _admin, CancellationToken.None);
        Assert.True(plan.CanExecute);
        Assert.True(plan.Destructive);
        Assert.True(plan.NeedsTypedDelete);
        Assert.Equal(new[] { "CopyUserData", "JellyfinDeleteFiles" }, plan.Steps.Select(s => s.Kind));
        Assert.Equal(18000, plan.BytesFreed);
        Assert.Contains("alex (1 item)", plan.Steps[0].Description, StringComparison.Ordinal);
        Assert.True(plan.SafeMode);

        // Safe mode is on by default: refused on the server.
        req.Token = plan.Token;
        req.TypedConfirmation = "DELETE";
        var refused = await Assert.ThrowsAsync<ToolkitException>(() => _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None));
        Assert.Equal("safe_mode", refused.Code);
        Assert.True(Directory.Exists(_stale.Path));

        _cfg.SafeMode = false;
        req.Token = (await _svc.PlanAsync(req, _admin, CancellationToken.None)).Token;
        req.TypedConfirmation = "yes";
        Assert.Equal("type_delete", (await Assert.ThrowsAsync<ToolkitException>(() => _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None))).Code);

        req.Token = (await _svc.PlanAsync(req, _admin, CancellationToken.None)).Token;
        req.TypedConfirmation = "DELETE";
        var done = await _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None);
        Assert.True(done.Executed);
        Assert.All(done.Steps, s => Assert.Equal("Done", s.Result));
        Assert.False(Directory.Exists(_stale.Path));
        Assert.True(Directory.Exists(_current.Path));
        Assert.Contains((_stale.Id, true), _lib.Removed);

        var target = _lib.GetEpisodes(_current.Id, false).First(e => e.SeasonNumber == 1 && e.EpisodeNumber == 3);
        Assert.True(_users.Data[(_alex, target.Id)].Played);
        Assert.Empty(_svc.GetCached()!.Groups);
        Assert.Contains(_log.Read(new ActivityFilter { Area = "Duplicates" }), e => e.Action == "Delete files" && e.Result == "OK");
        Assert.DoesNotContain(_sonarrHttp.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DryRun_ChangesNothing_AndIsLogged()
    {
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _current.Id, RemoveItemIds = { _stale.Id }, DeleteFiles = true, DryRun = true };
        req.Token = (await _svc.PlanAsync(req, _admin, CancellationToken.None)).Token;
        var r = await _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None);
        Assert.True(r.DryRun);
        Assert.All(r.Steps, s => Assert.Equal("WouldDo", s.Result));
        Assert.True(Directory.Exists(_stale.Path));
        Assert.Empty(_lib.Removed);
        Assert.Contains(_log.Read(new ActivityFilter()), e => e.Result == "DryRun");
    }

    [Fact]
    public async Task RemovingTheManagedCopy_GoesThroughSonarr_WithWarning()
    {
        _cfg.SafeMode = false;
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _stale.Id, RemoveItemIds = { _current.Id }, CopyUserData = false, DryRun = false, TypedConfirmation = "DELETE" };
        var plan = await _svc.PlanAsync(req, _admin, CancellationToken.None);
        Assert.Equal(new[] { "ArrDelete", "JellyfinRemove" }, plan.Steps.Select(s => s.Kind));
        Assert.Contains(plan.Warnings, w => w.Contains("is the one Sonarr manages", StringComparison.Ordinal));
        req.Token = plan.Token;

        var done = await _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None);
        Assert.True(done.Executed);
        var del = Assert.Single(_sonarrHttp.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal("http://sonarr:8989/api/v3/series/42?deleteFiles=true&addImportListExclusion=false", del.RequestUri!.ToString());
        Assert.Contains((_current.Id, false), _lib.Removed);
    }

    [Fact]
    public async Task CopyNotOnDisk_CannotBeDeleted_AndShowsManualCommand()
    {
        _cfg.SafeMode = false;
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        Directory.Delete(_stale.Path!, true);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _current.Id, RemoveItemIds = { _stale.Id }, DeleteFiles = true, CopyUserData = false, DryRun = false };
        var plan = await _svc.PlanAsync(req, _admin, CancellationToken.None);
        Assert.False(plan.CanExecute);
        Assert.Null(plan.Token);
        var step = Assert.Single(plan.Steps);
        Assert.False(step.Possible);
        Assert.Contains("Not found on disk", step.Blocker, StringComparison.Ordinal);
        Assert.StartsWith("docker exec -u 0 'jellyfin' rm -r -- '", Assert.Single(step.ManualCommands), StringComparison.Ordinal);
        Assert.DoesNotContain("PUID", step.Blocker, StringComparison.OrdinalIgnoreCase);

        // Library-only removal is still possible (the usual fix for a ghost copy).
        req.DeleteFiles = false;
        var libOnly = await _svc.PlanAsync(req, _admin, CancellationToken.None);
        Assert.True(libOnly.CanExecute);
        Assert.False(libOnly.Destructive);
        Assert.Contains(libOnly.Warnings, w => w.Contains("next library scan will add this copy back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LibraryOnly_WithIgnoreFile_WritesDotIgnore()
    {
        _cfg.SafeMode = false;
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _current.Id, RemoveItemIds = { _stale.Id }, Method = "LibraryOnly", WriteIgnoreFile = true, CopyUserData = false, DryRun = false };
        req.Token = (await _svc.PlanAsync(req, _admin, CancellationToken.None)).Token;
        var done = await _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None);
        Assert.Equal(new[] { "JellyfinRemove", "WriteIgnore" }, done.Steps.Select(s => s.Kind));
        Assert.True(File.Exists(Path.Combine(_stale.Path!, ".ignore")));
        Assert.Contains((_stale.Id, false), _lib.Removed);
        Assert.True(Directory.Exists(_stale.Path));
    }

    [Fact]
    public async Task PlanChangedAfterPreview_IsRefused()
    {
        _cfg.SafeMode = false;
        var g = Assert.Single((await _svc.ScanAsync("admin", null, CancellationToken.None)).Groups);
        var req = new RemovalRequest { GroupId = g.Id, KeepItemId = _current.Id, RemoveItemIds = { _stale.Id }, DeleteFiles = true, CopyUserData = true, DryRun = false, TypedConfirmation = "DELETE" };
        req.Token = (await _svc.PlanAsync(req, _admin, CancellationToken.None)).Token;
        var anotherEpisode = _lib.GetEpisodes(_stale.Id, false).First(e => e.EpisodeNumber == 5);
        _users.Data[(_alex, anotherEpisode.Id)] = new PlayState { Played = true };
        var ex = await Assert.ThrowsAsync<ToolkitException>(() => _svc.ExecuteAsync(req, _admin, "admin", CancellationToken.None));
        Assert.Equal("plan_changed", ex.Code);
        Assert.True(Directory.Exists(_stale.Path));
    }

    [Fact]
    public async Task SonarrDown_IsReported_AndConfidenceDropsWithoutManagedSignal()
    {
        var down = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        var store = new JsonStore(Path.Combine(_tmp.Path, "data2"));
        using var svc = new DuplicateService(_lib, _users, new FakeSpace(), new ArrFactory(() => new HttpClient(down), () => _cfg), store, _log, new SafetyGuard(() => _cfg), () => _cfg);
        var r = await svc.ScanAsync("admin", null, CancellationToken.None);
        Assert.Contains(r.Problems, p => p.StartsWith("Sonarr: Could not reach Sonarr", StringComparison.Ordinal));
        var g = Assert.Single(r.Groups);
        Assert.Equal(_current.Id, g.RecommendedKeepId);
        Assert.All(g.Copies, c => Assert.Null(c.ArrManaged));
        Assert.Contains(g.Notes, n => n.Contains("not configured or did not answer", StringComparison.Ordinal));
        Assert.Equal(Confidence.Medium, g.Confidence);
    }
}
