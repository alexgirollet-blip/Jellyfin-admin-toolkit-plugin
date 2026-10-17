using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_respond(request));
    }
}

public class ArrTests
{
    private const string SeriesJson = """
        [
          { "id": 7, "title": "The Office", "year": 2005, "path": "/tv/The Office (US)", "tvdbId": 73244, "tmdbId": 2316, "imdbId": "tt0386676", "monitored": true, "statistics": { "sizeOnDisk": 1234 } },
          { "id": 9, "title": "Bluey", "year": 2018, "path": "/tv/kids/Bluey", "tvdbId": 353546, "monitored": false }
        ]
        """;

    private static ArrClient Sonarr(FakeHandler h, string key = "secretkey123")
        => new(new HttpClient(h), ArrKind.Sonarr, "http://sonarr:8989/", key, new ArrPathMapper(new[]
        {
            new PathMapping { ArrPrefix = "/tv", JellyfinPrefix = "/data/8tb2/TV" },
            new PathMapping { ArrPrefix = "/tv/kids", JellyfinPrefix = "/data/1tb/Kids" }
        }));

    [Fact]
    public async Task List_MapsPathsToJellyfinView_AndSendsKeyInHeaderOnly()
    {
        var h = new FakeHandler(_ => FakeHandler.Json(SeriesJson));
        var list = await Sonarr(h).ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Equal("/data/8tb2/TV/The Office (US)", list[0].Path);
        Assert.Equal("/tv/The Office (US)", list[0].RawPath);
        Assert.Equal("/data/1tb/Kids/Bluey", list[1].Path);
        Assert.Equal(1234, list[0].SizeOnDisk);
        var req = Assert.Single(h.Requests);
        Assert.Equal("http://sonarr:8989/api/v3/series", req.RequestUri!.ToString());
        Assert.Equal("secretkey123", req.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain("secretkey123", req.RequestUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Match_UsesTvdbThenTmdbThenImdb()
    {
        var list = await Sonarr(new FakeHandler(_ => FakeHandler.Json(SeriesJson))).ListAsync(CancellationToken.None);
        var byTvdb = new ItemSnapshot { ProviderIds = { ["Tvdb"] = "73244" } };
        var byImdb = new ItemSnapshot { ProviderIds = { ["Imdb"] = "TT0386676" } };
        var none = new ItemSnapshot { ProviderIds = { ["Tvdb"] = "1" } };
        Assert.Equal(7, ArrClient.Match(list, byTvdb, ArrKind.Sonarr)!.Id);
        Assert.Equal(7, ArrClient.Match(list, byImdb, ArrKind.Sonarr)!.Id);
        Assert.Null(ArrClient.Match(list, none, ArrKind.Sonarr));
    }

    [Fact]
    public async Task Delete_UsesDocumentedQueryParameters_WithoutImportExclusion()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Sonarr(h).DeleteAsync(7, true, CancellationToken.None);
        Assert.Equal(HttpMethod.Delete, h.Requests[0].Method);
        Assert.Equal("http://sonarr:8989/api/v3/series/7?deleteFiles=true&addImportListExclusion=false", h.Requests[0].RequestUri!.ToString());

        var r = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await new ArrClient(new HttpClient(r), ArrKind.Radarr, "http://radarr:7878", "k", new ArrPathMapper(Array.Empty<PathMapping>())).DeleteAsync(3, false, CancellationToken.None);
        Assert.Equal("http://radarr:7878/api/v3/movie/3?deleteFiles=false&addImportExclusion=false", r.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Test_ReportsVersion_AndDetectsWrongApp()
    {
        var ok = Sonarr(new FakeHandler(_ => FakeHandler.Json("""{ "appName": "Sonarr", "version": "4.0.15.2941" }""")));
        Assert.Equal("Sonarr 4.0.15.2941", await ok.TestAsync(CancellationToken.None));

        var wrong = Sonarr(new FakeHandler(_ => FakeHandler.Json("""{ "appName": "Radarr", "version": "5.1" }""")));
        var ex = await Assert.ThrowsAsync<ArrException>(() => wrong.TestAsync(CancellationToken.None));
        Assert.Contains("answers as Radarr", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_AreReadable_AndNeverContainTheKey()
    {
        var unauthorized = Sonarr(new FakeHandler(_ => FakeHandler.Json("""{"message":"bad key secretkey123"}""", HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<ArrException>(() => unauthorized.ListAsync(CancellationToken.None));
        Assert.Contains("HTTP 401 (check the API key)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secretkey123", ex.Message, StringComparison.Ordinal);

        var html = Sonarr(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }));
        var ex2 = await Assert.ThrowsAsync<ArrException>(() => html.ListAsync(CancellationToken.None));
        Assert.Contains("not JSON", ex2.Message, StringComparison.Ordinal);

        var down = Sonarr(new FakeHandler(_ => throw new HttpRequestException("Connection refused")));
        var ex3 = await Assert.ThrowsAsync<ArrException>(() => down.ListAsync(CancellationToken.None));
        Assert.Contains("Could not reach Sonarr at http://sonarr:8989: Connection refused", ex3.Message, StringComparison.Ordinal);

        var unconfigured = new ArrClient(new HttpClient(new FakeHandler(_ => FakeHandler.Json("[]"))), ArrKind.Radarr, "", "", new ArrPathMapper(Array.Empty<PathMapping>()));
        Assert.False(unconfigured.IsConfigured);
        await Assert.ThrowsAsync<ArrException>(() => unconfigured.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Jellyseerr_Test_ChecksStatusAndKey()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("status", StringComparison.Ordinal)
            ? FakeHandler.Json("""{ "version": "2.7.3" }""")
            : FakeHandler.Json("{}"));
        Assert.Equal("Jellyseerr 2.7.3", await new JellyseerrClient(new HttpClient(h), "http://js:5055", "k").TestAsync(CancellationToken.None));
        Assert.Equal(2, h.Requests.Count);

        var bad = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("status", StringComparison.Ordinal) ? FakeHandler.Json("""{ "version": "2" }""") : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<ArrException>(() => new JellyseerrClient(new HttpClient(bad), "http://js:5055", "k").TestAsync(CancellationToken.None));
        Assert.Contains("check the API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseUrl_Validation()
    {
        Assert.Equal("http://x:1/sonarr", ArrClient.NormalizeBaseUrl(" http://x:1/sonarr/ "));
        Assert.Null(ArrClient.NormalizeBaseUrl("ftp://x"));
        Assert.Null(ArrClient.NormalizeBaseUrl("sonarr:8989"));
    }
}
