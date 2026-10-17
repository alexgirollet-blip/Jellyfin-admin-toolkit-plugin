using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Images;
using Jellyfin.Plugin.AdminToolkit.Integrations.ImageSearch;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public sealed class ImageServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeLibrary _lib = new();
    private readonly FakeImages _img = new();
    private readonly PluginConfiguration _cfg = new();
    private readonly FetchCache _cache = new();
    private readonly SafeFetcher _fetcher = new();
    private readonly ImageService _svc;
    private readonly ActivityLog _log;
    private readonly ItemSnapshot _movie;

    public ImageServiceTests()
    {
        _movie = new ItemSnapshot { Id = Guid.NewGuid(), Name = "The Matrix", Year = 1999, Kind = MediaKind.Movie, Path = "/m/matrix.mkv" };
        _lib.Items.Add(_movie);
        _lib.Items.Add(new ItemSnapshot { Id = Guid.NewGuid(), Name = "Heat", Year = 1995, Kind = MediaKind.Movie, ImageCounts = { ["Logo"] = 1, ["Primary"] = 1 } });
        _img.Slots.Add(new ImageSlot { Type = "Backdrop", Index = 0, Width = 1920, Height = 1080 });
        _img.Slots.Add(new ImageSlot { Type = "Backdrop", Index = 1, Width = 1920, Height = 1080 });
        _img.Slots.Add(new ImageSlot { Type = "Backdrop", Index = 2, Width = 1920, Height = 1080 });
        _log = new ActivityLog(_tmp.Path, () => 30);
        _svc = new ImageService(_lib, _img, _fetcher, _cache, () => new HttpClient(new FakeHandler(_ => FakeHandler.Json("{}"))), _log, new SafetyGuard(() => _cfg), () => _cfg);
    }

    public void Dispose()
    {
        _fetcher.Dispose();
        _tmp.Dispose();
    }

    [Fact]
    public async Task Apply_IsRefusedInSafeMode_AndWithoutConfirm()
    {
        var req = new ApplyImageRequest { Type = "Logo", DataBase64 = Convert.ToBase64String(TransparencyTests.Fixture("logo-transparent.png")), Confirm = true };
        Assert.Equal("safe_mode", (await Assert.ThrowsAsync<ToolkitException>(() => _svc.ApplyAsync(_movie.Id, req, "admin", CancellationToken.None))).Code);
        _cfg.SafeMode = false;
        req.Confirm = false;
        Assert.Equal("confirmation_required", (await Assert.ThrowsAsync<ToolkitException>(() => _svc.ApplyAsync(_movie.Id, req, "admin", CancellationToken.None))).Code);
        Assert.Empty(_img.Calls);
    }

    [Fact]
    public async Task Apply_Upload_SavesWithRealMimeType_AndLocks()
    {
        _cfg.SafeMode = false;
        var png = TransparencyTests.Fixture("logo-transparent.png");
        var req = new ApplyImageRequest { Type = "logo", DataBase64 = "data:image/png;base64," + Convert.ToBase64String(png), Confirm = true, Lock = true };
        await _svc.ApplyAsync(_movie.Id, req, "admin", CancellationToken.None);
        Assert.Equal($"save Logo null image/png {png.Length}", _img.Calls[0]);
        Assert.Equal("lock True", _img.Calls[1]);
        var entry = Assert.Single(_log.Read(new ActivityFilter { Area = "Images" }));
        Assert.Contains("Set Logo: 400x150 png, Transparent", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_FromCachedUrl_ReplaceBackdropAtIndex_AndAddBackdrop()
    {
        _cfg.SafeMode = false;
        var url = "https://image.tmdb.org/t/p/original/backdrop.png";
        _cache.Add(url, new FetchedImage(TransparencyTests.Fixture("backdrop.png"), "image/png", new Uri(url)));
        await _svc.ApplyAsync(_movie.Id, new ApplyImageRequest { Type = "Backdrop", Url = url, Index = 2, Confirm = true }, "admin", CancellationToken.None);
        await _svc.ApplyAsync(_movie.Id, new ApplyImageRequest { Type = "Backdrop", Url = url, Mode = "add", Confirm = true }, "admin", CancellationToken.None);
        Assert.StartsWith("save Backdrop 2 image/png", _img.Calls[0], StringComparison.Ordinal);
        Assert.StartsWith("save Backdrop null image/png", _img.Calls[1], StringComparison.Ordinal);
        await Assert.ThrowsAsync<ToolkitException>(() => _svc.ApplyAsync(_movie.Id, new ApplyImageRequest { Type = "Logo", Url = url, Mode = "add", Confirm = true }, "admin", CancellationToken.None));
    }

    [Fact]
    public async Task Apply_RejectsNonImageUpload()
    {
        _cfg.SafeMode = false;
        var req = new ApplyImageRequest { Type = "Primary", DataBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<svg/>")), Confirm = true };
        Assert.Equal("bad_image", (await Assert.ThrowsAsync<ToolkitException>(() => _svc.ApplyAsync(_movie.Id, req, "admin", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Reorder_MovesWithAdjacentSwaps()
    {
        _cfg.SafeMode = false;
        await _svc.ReorderAsync(_movie.Id, "Backdrop", 2, 0, "admin", CancellationToken.None);
        Assert.Equal(new[] { "swap Backdrop 2 1", "swap Backdrop 1 0" }, _img.Calls);
        await Assert.ThrowsAsync<ToolkitException>(() => _svc.ReorderAsync(_movie.Id, "Backdrop", 0, 3, "admin", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_ChecksTheImageExists()
    {
        _cfg.SafeMode = false;
        await _svc.DeleteAsync(_movie.Id, "Backdrop", 1, "admin", CancellationToken.None);
        Assert.Equal("delete Backdrop 1", _img.Calls.Single());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<ToolkitException>(() => _svc.DeleteAsync(_movie.Id, "Logo", 0, "admin", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Analyze_ReportsTransparencyAndWarnings_ForCachedImages()
    {
        _cache.Add("https://x/logo.png", new FetchedImage(TransparencyTests.Fixture("logo-opaque-with-alpha.png"), "image/png", new Uri("https://x/logo.png")));
        _cache.Add("https://x/t.png", new FetchedImage(TransparencyTests.Fixture("logo-transparent.png"), "image/png", new Uri("https://x/t.png")));
        var r = await _svc.AnalyzeAsync(new[] { "https://x/logo.png", "https://x/t.png", "http://127.0.0.1/x.png" }, "Logo", MediaKind.Movie, CancellationToken.None);
        Assert.Equal("Opaque", r[0].Transparency);
        Assert.Contains(r[0].Warnings, w => w.Contains("no transparent background", StringComparison.Ordinal));
        Assert.Equal("Transparent", r[1].Transparency);
        Assert.Empty(r[1].Warnings);
        Assert.False(r[2].Ok);
        Assert.Contains("private, loopback or link-local", r[2].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Suitability_ChecksAspectAndResolution()
    {
        Assert.Empty(ImageService.Suitability("Primary", MediaKind.Movie, 1000, 1500, Transparency.Opaque));
        Assert.Contains(ImageService.Suitability("Primary", MediaKind.Movie, 1920, 1080, Transparency.Opaque), w => w.StartsWith("Posters should be about 2:3", StringComparison.Ordinal));
        Assert.Contains(ImageService.Suitability("Primary", MediaKind.Movie, 400, 600, Transparency.Opaque), w => w.StartsWith("Low resolution for a poster", StringComparison.Ordinal));
        Assert.Contains(ImageService.Suitability("Backdrop", MediaKind.Movie, 1000, 1500, Transparency.Opaque), w => w.Contains("16:9", StringComparison.Ordinal));
        Assert.Empty(ImageService.Suitability("Primary", MediaKind.Episode, 1920, 1080, Transparency.Opaque));
        Assert.Contains(ImageService.Suitability("Disc", MediaKind.Movie, 1000, 1000, Transparency.Opaque), w => w.Contains("transparent", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultQuery_IsTitleYearAndType()
    {
        Assert.Equal("The Matrix 1999 poster", ImageService.DefaultQuery(_movie, "Primary"));
        Assert.Equal("The Matrix 1999 logo transparent png", ImageService.DefaultQuery(_movie, "Logo"));
        Assert.Equal("Show season 2 poster", ImageService.DefaultQuery(new ItemSnapshot { Kind = MediaKind.Season, SeriesName = "Show", SeasonNumber = 2 }, "Primary"));
    }

    [Fact]
    public void Missing_ListsItemsWithoutTheType()
    {
        var missing = _svc.Missing("Logo", new[] { MediaKind.Movie }, null);
        Assert.Equal("The Matrix", Assert.Single(missing).Name);
        Assert.Throws<ToolkitException>(() => _svc.Missing("Poster", new[] { MediaKind.Movie }, null));
    }

    [Fact]
    public async Task WebSearch_WithoutProvider_ExplainsSetup()
    {
        var r = JsonSerializer.Serialize(await _svc.SearchWebAsync(_movie.Id, "Logo", null, CancellationToken.None));
        Assert.Contains("\"configured\":false", r, StringComparison.Ordinal);
        Assert.Contains("Brave Search API", r, StringComparison.Ordinal);
        Assert.Contains("The Matrix 1999 logo transparent png", r, StringComparison.Ordinal);
    }
}

public class ImageSearchProviderTests
{
    [Fact]
    public async Task Brave_ParsesResults_AndSendsKeyAsHeader()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""
            { "type": "images", "results": [
              { "title": "Matrix logo", "url": "https://fanart.example/page", "thumbnail": { "src": "https://imgs.search.brave.com/t.jpg" },
                "properties": { "url": "https://fanart.example/logo.png", "width": 800, "height": 310 } },
              { "title": "no original", "properties": {} } ] }
            """));
        var r = await new BraveImageSearch(new HttpClient(h), "brave-key").SearchAsync("matrix logo", 20, CancellationToken.None);
        var hit = Assert.Single(r);
        Assert.Equal("https://fanart.example/logo.png", hit.Url);
        Assert.Equal(800, hit.Width);
        Assert.Equal("https://imgs.search.brave.com/t.jpg", hit.ThumbnailUrl);
        var req = Assert.Single(h.Requests);
        Assert.Equal("brave-key", req.Headers.GetValues("X-Subscription-Token").Single());
        Assert.StartsWith("https://api.search.brave.com/res/v1/images/search?q=matrix%20logo&count=20", req.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SerpApi_ParsesResults_AndScrubsKeyFromErrors()
    {
        var ok = new FakeHandler(_ => FakeHandler.Json("""
            { "images_results": [ { "original": "https://x/p.jpg", "original_width": 1000, "original_height": 1500, "thumbnail": "https://serpapi/t.jpg", "title": "Poster", "link": "https://x/page" } ] }
            """));
        var r = await new SerpApiImageSearch(new HttpClient(ok), "serp-key").SearchAsync("matrix poster", 10, CancellationToken.None);
        Assert.Equal(1500, Assert.Single(r).Height);

        var bad = new FakeHandler(req => FakeHandler.Json("{\"error\":\"Invalid API key serp-key\"}", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<ToolkitException>(() => new SerpApiImageSearch(new HttpClient(bad), "serp-key").SearchAsync("x", 10, CancellationToken.None));
        Assert.Contains("HTTP 401 (check the API key)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("serp-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoogleCse_NeedsEngineId_AndParsesItems()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{ "items": [ { "link": "https://x/a.png", "title": "A", "image": { "width": 600, "height": 900, "thumbnailLink": "https://t/a" } } ] }"""));
        await Assert.ThrowsAsync<ToolkitException>(() => new GoogleCseImageSearch(new HttpClient(h), "k", "").SearchAsync("x", 5, CancellationToken.None));
        var r = await new GoogleCseImageSearch(new HttpClient(h), "k", "cx1").SearchAsync("x", 5, CancellationToken.None);
        Assert.Equal(900, Assert.Single(r).Height);
    }

    [Fact]
    public void Factory_ReturnsNullWithoutKey()
    {
        var cfg = new PluginConfiguration { ImageSearchProvider = ImageSearchProviderKind.Brave };
        Assert.Null(ImageSearchFactory.Create(cfg, new HttpClient()));
        cfg.ImageSearchApiKey = "k";
        Assert.IsType<BraveImageSearch>(ImageSearchFactory.Create(cfg, new HttpClient()));
    }
}
