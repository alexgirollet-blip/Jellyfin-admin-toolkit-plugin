using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Integrations.Arr;

public enum ArrKind
{
    Sonarr,
    Radarr
}

/// <summary>A series (Sonarr) or movie (Radarr) entry.</summary>
public sealed class ArrTitle
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>Gets or sets the path as Sonarr/Radarr reports it.</summary>
    public string RawPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the path translated to Jellyfin's view with the configured path mappings.</summary>
    public string Path { get; set; } = string.Empty;

    public int? TvdbId { get; set; }

    public int? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public long SizeOnDisk { get; set; }

    public bool Monitored { get; set; }
}

/// <summary>Raised for HTTP/protocol failures talking to Sonarr/Radarr/Jellyseerr. Never contains the API key.</summary>
public sealed class ArrException : Exception
{
    public ArrException(string message)
        : base(message)
    {
    }

    public ArrException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ArrException()
    {
    }
}

/// <summary>
/// Sonarr / Radarr v3 API client (Sonarr v4 still serves /api/v3). Endpoints checked against the projects' OpenAPI specs:
/// GET /api/v3/system/status, GET /api/v3/series, GET /api/v3/movie,
/// DELETE /api/v3/series/{id}?deleteFiles=&amp;addImportListExclusion=, DELETE /api/v3/movie/{id}?deleteFiles=&amp;addImportExclusion=.
/// The API key is sent in the X-Api-Key header, never in the URL.
/// </summary>
public sealed class ArrClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ArrPathMapper _paths;

    public ArrClient(HttpClient http, ArrKind kind, string baseUrl, string apiKey, ArrPathMapper paths)
    {
        _http = http;
        Kind = kind;
        _baseUrl = NormalizeBaseUrl(baseUrl) ?? string.Empty;
        _apiKey = (apiKey ?? string.Empty).Trim();
        _paths = paths;
    }

    public ArrKind Kind { get; }

    public string Name => Kind.ToString();

    public bool IsConfigured => _baseUrl.Length > 0 && _apiKey.Length > 0;

    /// <summary>Returns a trimmed absolute http(s) URL without trailing slash, or null if invalid.</summary>
    public static string? NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim().TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? url : null;
    }

    /// <summary>GET system/status; returns e.g. "Sonarr 4.0.15.2941".</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/api/v3/system/status", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var app = Str(root, "appName") ?? Name;
        if (!string.Equals(app, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArrException($"The URL answers as {app}, not {Name}. Check which URL is in which field.");
        }

        return $"{app} {Str(root, "version")}".Trim();
    }

    /// <summary>All series (Sonarr) or movies (Radarr), with paths translated to Jellyfin's view.</summary>
    public async Task<IReadOnlyList<ArrTitle>> ListAsync(CancellationToken ct)
    {
        using var doc = await GetAsync(Kind == ArrKind.Sonarr ? "/api/v3/series" : "/api/v3/movie", ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArrException($"{Name} returned an unexpected answer for its {(Kind == ArrKind.Sonarr ? "series" : "movie")} list.");
        }

        var list = new List<ArrTitle>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var raw = Str(e, "path") ?? string.Empty;
            list.Add(new ArrTitle
            {
                Id = Int(e, "id") ?? 0,
                Title = Str(e, "title") ?? "?",
                Year = Int(e, "year"),
                RawPath = raw,
                Path = raw.Length == 0 ? string.Empty : _paths.ToJellyfin(raw),
                TvdbId = Int(e, "tvdbId") is > 0 ? Int(e, "tvdbId") : null,
                TmdbId = Int(e, "tmdbId") is > 0 ? Int(e, "tmdbId") : null,
                ImdbId = string.IsNullOrWhiteSpace(Str(e, "imdbId")) ? null : Str(e, "imdbId"),
                SizeOnDisk = Long(e, "sizeOnDisk") ?? (e.TryGetProperty("statistics", out var st) ? Long(st, "sizeOnDisk") ?? 0 : 0),
                Monitored = e.TryGetProperty("monitored", out var m) && m.ValueKind == JsonValueKind.True
            });
        }

        return list;
    }

    /// <summary>Finds the entry for a Jellyfin item by TVDb (Sonarr) / TMDb (Radarr) id, then the other ids.</summary>
    public static ArrTitle? Match(IReadOnlyList<ArrTitle> titles, ItemSnapshot item, ArrKind kind)
    {
        int? Parse(string? v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var tvdb = Parse(item.ProviderId("Tvdb"));
        var tmdb = Parse(item.ProviderId("Tmdb"));
        var imdb = item.ProviderId("Imdb");
        ArrTitle? hit = null;
        if (kind == ArrKind.Sonarr && tvdb is not null)
        {
            hit = titles.FirstOrDefault(t => t.TvdbId == tvdb);
        }

        hit ??= tmdb is null ? null : titles.FirstOrDefault(t => t.TmdbId == tmdb);
        hit ??= imdb is null ? null : titles.FirstOrDefault(t => string.Equals(t.ImdbId, imdb, StringComparison.OrdinalIgnoreCase));
        if (hit is null && kind == ArrKind.Radarr && tvdb is not null)
        {
            hit = titles.FirstOrDefault(t => t.TvdbId == tvdb);
        }

        return hit;
    }

    /// <summary>
    /// Deletes the series/movie in Sonarr/Radarr. With <paramref name="deleteFiles"/> Sonarr/Radarr deletes its folder.
    /// No import exclusion is added, so the title can be added again later.
    /// </summary>
    public async Task DeleteAsync(int id, bool deleteFiles, CancellationToken ct)
    {
        var flag = deleteFiles ? "true" : "false";
        var path = Kind == ArrKind.Sonarr
            ? $"/api/v3/series/{id.ToString(CultureInfo.InvariantCulture)}?deleteFiles={flag}&addImportListExclusion=false"
            : $"/api/v3/movie/{id.ToString(CultureInfo.InvariantCulture)}?deleteFiles={flag}&addImportExclusion=false";
        using var req = Build(HttpMethod.Delete, path);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
    }

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    internal static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Get, pathAndQuery);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ArrException($"{Name} returned something that is not JSON from {pathAndQuery}. Check the base URL (including any URL base such as /sonarr).", ex);
        }
    }

    private HttpRequestMessage Build(HttpMethod method, string pathAndQuery)
    {
        if (!IsConfigured)
        {
            throw new ArrException($"{Name} is not configured (URL and API key are needed).");
        }

        var req = new HttpRequestMessage(method, _baseUrl + pathAndQuery);
        req.Headers.Add("X-Api-Key", _apiKey);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ArrException($"Could not reach {Name} at {_baseUrl}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ArrException($"{Name} did not answer within 30 seconds.", ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        string hint = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " (check the API key)",
            HttpStatusCode.NotFound => " (check the base URL, including any URL base such as /sonarr)",
            _ => string.Empty
        };
        string snippet = string.Empty;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            snippet = body.Length > 200 ? body[..200] : body;
        }
        catch (HttpRequestException)
        {
        }

        var text = $"{Name} answered HTTP {(int)resp.StatusCode}{hint}. {snippet}".Trim();
        throw new ArrException(text.Replace(_apiKey, "***", StringComparison.Ordinal));
    }
}

/// <summary>Jellyseerr: connection test only (duplicate removal keeps one copy, so requests stay valid).</summary>
public sealed class JellyseerrClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public JellyseerrClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = ArrClient.NormalizeBaseUrl(baseUrl) ?? string.Empty;
        _apiKey = (apiKey ?? string.Empty).Trim();
    }

    public bool IsConfigured => _baseUrl.Length > 0 && _apiKey.Length > 0;

    /// <summary>GET /api/v1/status (version) then GET /api/v1/settings/main (validates the key).</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new ArrException("Jellyseerr is not configured (URL and API key are needed).");
        }

        string version = "?";
        foreach (var path in new[] { "/api/v1/status", "/api/v1/settings/main" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            req.Headers.Add("X-Api-Key", _apiKey);
            HttpResponseMessage resp;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ArrException($"Could not reach Jellyseerr at {_baseUrl}: {ex.Message}", ex);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    throw new ArrException($"Jellyseerr answered HTTP {(int)resp.StatusCode} for {path}{(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " (check the API key)" : string.Empty)}.");
                }

                if (path.EndsWith("status", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    version = ArrClient.Str(doc.RootElement, "version") ?? "?";
                }
            }
        }

        return "Jellyseerr " + version;
    }
}
