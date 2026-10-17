using System;
using System.Net.Http;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Integrations.Arr;

/// <summary>Creates Sonarr/Radarr/Jellyseerr clients from the current settings.</summary>
public sealed class ArrFactory
{
    private readonly Func<HttpClient> _http;
    private readonly Func<PluginConfiguration> _config;

    public ArrFactory(Func<HttpClient> http, Func<PluginConfiguration> config)
    {
        _http = http;
        _config = config;
    }

    public ArrClient Sonarr()
    {
        var c = _config();
        return new ArrClient(_http(), ArrKind.Sonarr, c.SonarrUrl, c.SonarrApiKey, new ArrPathMapper(c.ArrPathMappings));
    }

    public ArrClient Radarr()
    {
        var c = _config();
        return new ArrClient(_http(), ArrKind.Radarr, c.RadarrUrl, c.RadarrApiKey, new ArrPathMapper(c.ArrPathMappings));
    }

    public ArrClient For(MediaKind kind) => kind == MediaKind.Series ? Sonarr() : Radarr();

    public JellyseerrClient Jellyseerr()
    {
        var c = _config();
        return new JellyseerrClient(_http(), c.JellyseerrUrl, c.JellyseerrApiKey);
    }
}
