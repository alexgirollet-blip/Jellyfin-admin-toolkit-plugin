using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Api;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

/// <summary>
/// Writes real API responses (from the real services, serialised with the controllers' JSON options) for the
/// browser smoke test, so the page is tested against the actual JSON shapes. Writes only when
/// ADMIN_TOOLKIT_SAMPLE_OUT is set (CI and local runs set it to tests/client/fixtures/api-sample.json).
/// </summary>
public sealed class ApiSampleExport : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task WriteSample()
    {
        var target = Environment.GetEnvironmentVariable("ADMIN_TOOLKIT_SAMPLE_OUT");
        var root = Path.Combine(_tmp.Path, "media");
        var lib = new FakeLibrary();
        var users = new FakeUsers();
        var cfg = new PluginConfiguration
        {
            SonarrUrl = "http://sonarr:8989",
            SonarrApiKey = "sonarr-secret-1234",
            ArrPathMappings = { new PathMapping { ArrPrefix = "/tv", JellyfinPrefix = "/data/8tb2/TV" } },
            DriveMappings = { new DriveMapping { Prefix = Path.Combine(root, "8tb1"), Label = "8TB 1" }, new DriveMapping { Prefix = Path.Combine(root, "8tb2"), Label = "8TB 2" } }
        };
        var tvLib = new LibraryInfo { Id = Guid.NewGuid(), Name = "TV Shows", CollectionType = "tvshows", Locations = { Path.Combine(root, "8tb1", "TV"), Path.Combine(root, "8tb2", "TV") } };
        lib.Libraries.Add(tvLib);
        var stale = lib.AddSeries("Brooklyn Nine-Nine", 2013, Path.Combine(root, "8tb1", "TV", "Brooklyn Nine-Nine"), 18, 24, true, ("Tvdb", "269586"), ("Imdb", "tt2467372"));
        var current = lib.AddSeries("Brooklyn Nine-Nine", 2013, Path.Combine(root, "8tb2", "TV", "Brooklyn Nine-Nine"), 24, 24, true, ("Tvdb", "269586"), ("Imdb", "tt2467372"));
        foreach (var s in new[] { stale, current })
        {
            s.LibraryName = "TV Shows";
        }

        var alex = Guid.NewGuid();
        users.Users.Add(new UserRef(alex, "alex", true, false));
        users.Users.Add(new UserRef(Guid.NewGuid(), "sam", false, false));
        users.Data[(alex, lib.GetEpisodes(stale.Id, false)[2].Id)] = new PlayState { Played = true, PlayCount = 1, LastPlayedDate = new DateTime(2025, 3, 1) };
        var store = new JsonStore(Path.Combine(_tmp.Path, "data"));
        var log = new ActivityLog(store.Directory, () => 30);
        var sonarr = new FakeHandler(_ => FakeHandler.Json($$"""[{ "id": 12, "title": "Brooklyn Nine-Nine", "year": 2013, "path": "/tv/Brooklyn Nine-Nine", "tvdbId": 269586 }]"""));
        cfg.ArrPathMappings[0].JellyfinPrefix = Path.Combine(root, "8tb2", "TV");
        var guard = new SafetyGuard(() => cfg);
        using var dup = new DuplicateService(lib, users, new FakeSpace(), new ArrFactory(() => new HttpClient(sonarr), () => cfg), store, log, guard, () => cfg);
        var scan = await dup.ScanAsync("alex", null, CancellationToken.None);
        var g = scan.Groups[0];
        var plan = await dup.PlanAsync(new RemovalRequest { GroupId = g.Id, KeepItemId = g.KeepId, RemoveItemIds = g.Copies.Where(c => c.Item.Id != g.KeepId).Select(c => c.Item.Id).ToList(), DeleteFiles = true }, Guid.NewGuid(), CancellationToken.None);
        using var health = new HealthService(lib, store, log, guard, () => cfg);
        var report = await health.ScanAsync("alex", null, CancellationToken.None);
        using var storage = new StorageService(lib, users, new FakeSpace(), store, log, () => cfg, () => scan);
        var st = await storage.ScanAsync("alex", null, CancellationToken.None);
        var settings = new SettingsService(() => cfg, c => cfg = c, log, () => new HttpClient());

        var sample = new Dictionary<string, object?>
        {
            ["status"] = new { pluginVersion = "1.0.0.0", builtForJellyfin = ErrorText.BuiltForJellyfin, serverAssemblies = "10.11.10.0", safeMode = true, dryRunDefault = true, confirmationLevel = "TypeDelete", processUser = "uid 1000, gid 1000", dataDirectory = "/config/data/admintoolkit", scans = new ScanRunner().All() },
            ["duplicates"] = new { scanned = true, result = scan, marked = new Dictionary<string, List<Guid>>(), scan = new ScanState { Kind = "duplicates" } },
            ["plan"] = plan,
            ["health"] = new { scanned = true, report, scan = new ScanState { Kind = "health" } },
            ["storage"] = new { scanned = true, report = st, notWatchedMonths = 12, notWatched = storage.NotWatched(12), scan = new ScanState { Kind = "storage" } },
            ["drives"] = storage.Drives(),
            ["libraries"] = lib.Libraries,
            ["settings"] = new { settings = settings.GetMasked(), defaults = new ScoreWeights() },
            ["activity"] = log.Read(new ActivityFilter()),
            ["imageItem"] = new
            {
                item = new ImageItemSummary { Id = current.Id, Name = current.Name, Year = current.Year, Kind = MediaKind.Series, LibraryName = "TV Shows" },
                path = current.Path,
                locked = false,
                images = new[] { new ImageSlot { Type = "Primary", Index = 0, Width = 1000, Height = 1500, FileSize = 345678, Path = "/config/metadata/poster.jpg", IsLocal = true }, new ImageSlot { Type = "Backdrop", Index = 0, Width = 1920, Height = 1080 }, new ImageSlot { Type = "Backdrop", Index = 1, Width = 3840, Height = 2160 } },
                remoteProviders = new[] { "TheMovieDb", "Fanart", "TheTVDB" },
                remoteProvidersError = (string?)null,
                types = ImageService.EditableTypes,
                defaultQueries = ImageService.EditableTypes.ToDictionary(t => t, t => ImageService.DefaultQuery(current, t)),
                webSearchConfigured = false
            }
        };
        var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions(ToolkitControllerBase.Options) { WriteIndented = true });
        Assert.DoesNotContain("sonarr-secret-1234", json, StringComparison.Ordinal);
        Assert.Contains("\"confidence\": \"High\"", json, StringComparison.Ordinal);
        json = json.Replace(_tmp.Path, "/data-sample", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await File.WriteAllTextAsync(target, json);
        }
    }
}
