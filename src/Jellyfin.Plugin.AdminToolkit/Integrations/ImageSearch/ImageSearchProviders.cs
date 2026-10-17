using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Integrations.ImageSearch;

/// <summary>One web image search hit. The key used to search never leaves the server.</summary>
public sealed class WebImageResult
{
    public string Url { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Title { get; set; }

    public string? SourcePage { get; set; }

    public string Provider { get; set; } = string.Empty;
}

/// <summary>A pluggable web image search provider.</summary>
public interface IImageSearchProvider
{
    string Name { get; }

    Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int count, CancellationToken ct);
}

/// <summary>
/// Brave Search API, image search: GET https://api.search.brave.com/res/v1/images/search?q=&amp;count= with header
/// X-Subscription-Token. Results: results[].properties.url (original), properties.width/height (optional),
/// thumbnail.src, title, url (source page). count max 200.
/// </summary>
public sealed class BraveImageSearch : IImageSearchProvider
{
    private readonly HttpClient _http;
    private readonly string _key;

    public BraveImageSearch(HttpClient http, string key)
    {
        _http = http;
        _key = key;
    }

    public string Name => "Brave Search";

    public async Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var url = "https://api.search.brave.com/res/v1/images/search?q=" + Uri.EscapeDataString(query)
                  + "&count=" + Math.Clamp(count, 1, 100).ToString(CultureInfo.InvariantCulture) + "&safesearch=strict";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Subscription-Token", _key);
        req.Headers.Accept.ParseAdd("application/json");
        using var doc = await ImageSearchHttp.SendAsync(_http, req, Name, ct).ConfigureAwait(false);
        var list = new List<WebImageResult>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var props = r.TryGetProperty("properties", out var p) ? p : default;
                var original = ImageSearchHttp.Str(props, "url");
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }

                list.Add(new WebImageResult
                {
                    Url = original,
                    ThumbnailUrl = r.TryGetProperty("thumbnail", out var t) ? ImageSearchHttp.Str(t, "src") : null,
                    Width = ImageSearchHttp.Int(props, "width"),
                    Height = ImageSearchHttp.Int(props, "height"),
                    Title = ImageSearchHttp.Str(r, "title"),
                    SourcePage = ImageSearchHttp.Str(r, "url"),
                    Provider = Name
                });
            }
        }

        return list;
    }
}

/// <summary>
/// SerpApi Google Images: GET https://serpapi.com/search.json?engine=google_images&amp;q=&amp;api_key=.
/// Results: images_results[].original, original_width, original_height, thumbnail, title, link.
/// SerpApi only accepts the key as a query parameter; the request is made server-side and never logged.
/// </summary>
public sealed class SerpApiImageSearch : IImageSearchProvider
{
    private readonly HttpClient _http;
    private readonly string _key;

    public SerpApiImageSearch(HttpClient http, string key)
    {
        _http = http;
        _key = key;
    }

    public string Name => "SerpApi (Google Images)";

    public async Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        var url = "https://serpapi.com/search.json?engine=google_images&safe=active&q=" + Uri.EscapeDataString(query) + "&api_key=" + Uri.EscapeDataString(_key);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var doc = await ImageSearchHttp.SendAsync(_http, req, Name, ct, _key).ConfigureAwait(false);
        if (ImageSearchHttp.Str(doc.RootElement, "error") is { } err)
        {
            throw new ToolkitException("search_failed", "SerpApi: " + err, 502);
        }

        var list = new List<WebImageResult>();
        if (doc.RootElement.TryGetProperty("images_results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var original = ImageSearchHttp.Str(r, "original");
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }

                list.Add(new WebImageResult
                {
                    Url = original,
                    ThumbnailUrl = ImageSearchHttp.Str(r, "thumbnail"),
                    Width = ImageSearchHttp.Int(r, "original_width"),
                    Height = ImageSearchHttp.Int(r, "original_height"),
                    Title = ImageSearchHttp.Str(r, "title"),
                    SourcePage = ImageSearchHttp.Str(r, "link"),
                    Provider = Name
                });
                if (list.Count >= count)
                {
                    break;
                }
            }
        }

        return list;
    }
}

/// <summary>
/// Google Custom Search JSON API (legacy): GET https://www.googleapis.com/customsearch/v1?key=&amp;cx=&amp;q=&amp;searchType=image.
/// Closed to new customers; Google ends it for existing customers on 1 January 2027. Offered only for existing keys.
/// </summary>
public sealed class GoogleCseImageSearch : IImageSearchProvider
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _cx;

    public GoogleCseImageSearch(HttpClient http, string key, string cx)
    {
        _http = http;
        _key = key;
        _cx = cx;
    }

    public string Name => "Google Custom Search (legacy, ends 1 Jan 2027)";

    public async Task<IReadOnlyList<WebImageResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cx))
        {
            throw new ToolkitException("search_not_configured", "Google Custom Search needs the search engine id (cx) as well as the API key.");
        }

        var url = "https://www.googleapis.com/customsearch/v1?searchType=image&safe=active&num=" + Math.Clamp(count, 1, 10).ToString(CultureInfo.InvariantCulture)
                  + "&q=" + Uri.EscapeDataString(query) + "&cx=" + Uri.EscapeDataString(_cx) + "&key=" + Uri.EscapeDataString(_key);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var doc = await ImageSearchHttp.SendAsync(_http, req, Name, ct, _key).ConfigureAwait(false);
        var list = new List<WebImageResult>();
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in items.EnumerateArray())
            {
                var img = r.TryGetProperty("image", out var i) ? i : default;
                list.Add(new WebImageResult
                {
                    Url = ImageSearchHttp.Str(r, "link") ?? string.Empty,
                    ThumbnailUrl = ImageSearchHttp.Str(img, "thumbnailLink"),
                    Width = ImageSearchHttp.Int(img, "width"),
                    Height = ImageSearchHttp.Int(img, "height"),
                    Title = ImageSearchHttp.Str(r, "title"),
                    SourcePage = ImageSearchHttp.Str(img, "contextLink"),
                    Provider = Name
                });
            }
        }

        list.RemoveAll(x => x.Url.Length == 0);
        return list;
    }
}

/// <summary>Picks the configured provider.</summary>
public static class ImageSearchFactory
{
    public const string SetupHelp =
        "No web image search provider is configured. In Settings > Image search choose Brave Search API (https://brave.com/search/api/, " +
        "paid per request with monthly credits) or SerpApi (https://serpapi.com/, Google Images results) and paste the API key. " +
        "Google's Custom Search JSON API is closed to new customers and ends on 1 January 2027.";

    public static IImageSearchProvider? Create(PluginConfiguration cfg, HttpClient http)
        => cfg.ImageSearchProvider switch
        {
            ImageSearchProviderKind.Brave when cfg.ImageSearchApiKey.Length > 0 => new BraveImageSearch(http, cfg.ImageSearchApiKey),
            ImageSearchProviderKind.SerpApi when cfg.ImageSearchApiKey.Length > 0 => new SerpApiImageSearch(http, cfg.ImageSearchApiKey),
            ImageSearchProviderKind.GoogleCse when cfg.ImageSearchApiKey.Length > 0 => new GoogleCseImageSearch(http, cfg.ImageSearchApiKey, cfg.GoogleSearchEngineId),
            _ => null
        };
}

internal static class ImageSearchHttp
{
    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage req, string name, CancellationToken ct, string? secretInUrl = null)
    {
        HttpResponseMessage resp;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            resp = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolkitException("search_failed", $"Could not reach {name}: {Scrub(ex.Message, secretInUrl)}", 502);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolkitException("search_failed", $"{name} did not answer within 20 seconds.", 504);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var hint = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " (check the API key)",
                    HttpStatusCode.TooManyRequests => " (rate limit or monthly quota reached)",
                    _ => string.Empty
                };
                var snippet = body.Length > 200 ? body[..200] : body;
                throw new ToolkitException("search_failed", Scrub($"{name} answered HTTP {(int)resp.StatusCode}{hint}. {snippet}".Trim(), secretInUrl), 502);
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new ToolkitException("search_failed", $"{name} returned something that is not JSON.", 502);
            }
        }
    }

    public static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
        {
            return i;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : null;
    }

    private static string Scrub(string text, string? secret)
        => string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
}
