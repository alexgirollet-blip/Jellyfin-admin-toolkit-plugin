using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AdminToolkit;

/// <summary>
/// Admin Toolkit. A dashboard page only: no home-screen footprint, nothing injected into jellyfin-web, no writes into other
/// plugins' settings, and nothing runs at server start-up (the constructor only loads the configuration).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "41dca3a8-262c-4dd5-b4b1-ee38bef2c31d";

    public const string PluginName = "Admin Toolkit";

    private readonly IApplicationPaths _paths;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _paths = applicationPaths;
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Admin-only toolkit: duplicates, artwork, library health and storage insight. Read-only by default.";

    /// <summary>Raised after settings are saved.</summary>
    public event EventHandler? ConfigurationSaved;

    /// <summary>Gets the folder for cached scan results and the activity log: &lt;data&gt;/admintoolkit.</summary>
    public static string DataDirectory(IApplicationPaths paths) => Path.Combine(paths.DataPath, "admintoolkit");

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration c)
        {
            c.Normalize();
        }

        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by Jellyfin when the plugin is uninstalled. Removes the plugin's own data folder (cache + activity log).
    /// Jellyfin itself removes the plugin folder and its configuration XML. Admin Toolkit never writes anywhere else
    /// (no other plugin's settings, no jellyfin-web files), so nothing is left behind.
    /// </summary>
    public override void OnUninstalling()
    {
        try
        {
            var dir = DataDirectory(_paths);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Uninstall must always complete; a leftover cache folder is harmless.
        }

        base.OnUninstalling();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "AdminToolkit",
                DisplayName = PluginName,
                EmbeddedResourcePath = ns + ".Web.adminToolkit.html",
                EnableInMainMenu = true,
                MenuIcon = "construction"
            }
        };
    }
}
