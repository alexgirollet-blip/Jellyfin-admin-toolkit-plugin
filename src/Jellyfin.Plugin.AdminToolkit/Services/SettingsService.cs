using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Integrations.ImageSearch;
using Jellyfin.Plugin.AdminToolkit.Storage;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>Body of the "test connection" calls: optional unsaved values (a masked key means "use the saved key").</summary>
public sealed class ConnectionTestRequest
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    public string? Provider { get; set; }

    public string? EngineId { get; set; }

    public string? Query { get; set; }
}

/// <summary>Reads and saves settings with API keys masked; logs what changed (never secret values).</summary>
public sealed class SettingsService
{
    private static readonly string[] SecretProps = { nameof(PluginConfiguration.SonarrApiKey), nameof(PluginConfiguration.RadarrApiKey), nameof(PluginConfiguration.JellyseerrApiKey), nameof(PluginConfiguration.ImageSearchApiKey) };

    private readonly Func<PluginConfiguration> _get;
    private readonly Action<PluginConfiguration> _save;
    private readonly ActivityLog _log;
    private readonly Func<System.Net.Http.HttpClient> _http;

    public SettingsService(Func<PluginConfiguration> get, Action<PluginConfiguration> save, ActivityLog log, Func<System.Net.Http.HttpClient> http)
    {
        _get = get;
        _save = save;
        _log = log;
        _http = http;
    }

    /// <summary>The settings with every API key replaced by its mask.</summary>
    public PluginConfiguration GetMasked()
    {
        var copy = Clone(_get());
        foreach (var name in SecretProps)
        {
            var p = typeof(PluginConfiguration).GetProperty(name)!;
            p.SetValue(copy, Secrets.Mask((string?)p.GetValue(copy)));
        }

        return copy;
    }

    public PluginConfiguration Save(PluginConfiguration incoming, string user)
    {
        var current = _get();
        foreach (var name in SecretProps)
        {
            var p = typeof(PluginConfiguration).GetProperty(name)!;
            p.SetValue(incoming, Secrets.Merge((string?)p.GetValue(incoming), (string?)p.GetValue(current) ?? string.Empty));
        }

        incoming.Weights ??= new ScoreWeights();
        incoming.Normalize();
        var changes = Diff(current, incoming);
        _save(incoming);
        _log.Add(user, "Settings", "Save settings", "Admin Toolkit", "OK", changes.Count == 0 ? "No changes." : "Changed: " + string.Join("; ", changes));
        return GetMasked();
    }

    public PluginConfiguration ResetWeights(string user)
    {
        var c = Clone(_get());
        c.Weights = new ScoreWeights();
        return Save(c, user);
    }

    public async Task<string> TestArrAsync(string service, ConnectionTestRequest req, CancellationToken ct)
    {
        var cfg = _get();
        switch (service.ToLowerInvariant())
        {
            case "sonarr":
                return await new ArrClient(_http(), ArrKind.Sonarr, req.Url ?? cfg.SonarrUrl, Secrets.Merge(req.ApiKey, cfg.SonarrApiKey), new ArrPathMapper(cfg.ArrPathMappings)).TestAsync(ct).ConfigureAwait(false);
            case "radarr":
                return await new ArrClient(_http(), ArrKind.Radarr, req.Url ?? cfg.RadarrUrl, Secrets.Merge(req.ApiKey, cfg.RadarrApiKey), new ArrPathMapper(cfg.ArrPathMappings)).TestAsync(ct).ConfigureAwait(false);
            case "jellyseerr":
                return await new JellyseerrClient(_http(), req.Url ?? cfg.JellyseerrUrl, Secrets.Merge(req.ApiKey, cfg.JellyseerrApiKey)).TestAsync(ct).ConfigureAwait(false);
            default:
                throw new ToolkitException("bad_service", "Unknown service " + service);
        }
    }

    public async Task<object> TestImageSearchAsync(ConnectionTestRequest req, CancellationToken ct)
    {
        var cfg = Clone(_get());
        if (!string.IsNullOrEmpty(req.Provider) && Enum.TryParse<ImageSearchProviderKind>(req.Provider, true, out var kind))
        {
            cfg.ImageSearchProvider = kind;
        }

        cfg.ImageSearchApiKey = Secrets.Merge(req.ApiKey, cfg.ImageSearchApiKey);
        if (req.EngineId is not null)
        {
            cfg.GoogleSearchEngineId = req.EngineId.Trim();
        }

        var provider = ImageSearchFactory.Create(cfg, _http()) ?? throw new ToolkitException("search_not_configured", ImageSearchFactory.SetupHelp);
        var q = string.IsNullOrWhiteSpace(req.Query) ? "The Matrix 1999 poster" : req.Query.Trim();
        var results = await provider.SearchAsync(q, 5, ct).ConfigureAwait(false);
        return new { ok = true, provider = provider.Name, query = q, count = results.Count, sample = results.Take(3).Select(r => new { r.Url, r.Width, r.Height, r.Title }) };
    }

    /// <summary>Paths an admin may write-test: configured drive prefixes and library folders only.</summary>
    public static bool IsTestablePath(string path, PluginConfiguration cfg, IEnumerable<string> libraryLocations)
        => cfg.DriveMappings.Any(d => PathText.SamePath(d.Prefix, path)) || libraryLocations.Any(l => PathText.SamePath(l, path));

    private static PluginConfiguration Clone(PluginConfiguration c)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(c, JsonStore.Options), JsonStore.Options)!;

    private static List<string> Diff(PluginConfiguration a, PluginConfiguration b)
    {
        var changes = new List<string>();
        foreach (var p in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            var va = JsonSerializer.Serialize(p.GetValue(a), JsonStore.Options);
            var vb = JsonSerializer.Serialize(p.GetValue(b), JsonStore.Options);
            if (va == vb)
            {
                continue;
            }

            changes.Add(SecretProps.Contains(p.Name) ? p.Name + " changed" : $"{p.Name}: {Short(va)} -> {Short(vb)}");
        }

        return changes;
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] + "..." : s;
}
