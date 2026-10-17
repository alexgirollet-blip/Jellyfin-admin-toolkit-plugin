using System.Net.Http;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Images;
using Jellyfin.Plugin.AdminToolkit.Integrations.Arr;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AdminToolkit;

/// <summary>Current settings, from the plugin instance (safe defaults if it is not loaded yet).</summary>
internal static class PluginState
{
    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}

/// <summary>
/// Registers Admin Toolkit services. Everything is a lazily created singleton: nothing is constructed, read or written
/// until an admin opens the page or the (opt-in) scheduled task runs. No hosted services, no start-up tasks.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public const string HttpClientName = "AdminToolkit";

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientName);
        serviceCollection.AddSingleton<ILibraryAdapter, JellyfinLibraryAdapter>();
        serviceCollection.AddSingleton<IUserAdapter, JellyfinUserAdapter>();
        serviceCollection.AddSingleton<IImageAdapter, JellyfinImageAdapter>();
        serviceCollection.AddSingleton<IDriveSpaceProvider, SystemDriveSpace>();
        serviceCollection.AddSingleton(sp => new JsonStore(Plugin.DataDirectory(sp.GetRequiredService<IApplicationPaths>())));
        serviceCollection.AddSingleton(sp => new ActivityLog(Plugin.DataDirectory(sp.GetRequiredService<IApplicationPaths>()), () => PluginState.Config.ActivityRetentionDays));
        serviceCollection.AddSingleton(_ => new SafetyGuard(() => PluginState.Config));
        serviceCollection.AddSingleton(sp => new ArrFactory(() => Http(sp), () => PluginState.Config));
        serviceCollection.AddSingleton<SafeFetcher>();
        serviceCollection.AddSingleton<FetchCache>();
        serviceCollection.AddSingleton<ScanRunner>();
        serviceCollection.AddSingleton<SelfCheckService>();
        serviceCollection.AddSingleton(sp => new DuplicateService(
            sp.GetRequiredService<ILibraryAdapter>(),
            sp.GetRequiredService<IUserAdapter>(),
            sp.GetRequiredService<IDriveSpaceProvider>(),
            sp.GetRequiredService<ArrFactory>(),
            sp.GetRequiredService<JsonStore>(),
            sp.GetRequiredService<ActivityLog>(),
            sp.GetRequiredService<SafetyGuard>(),
            () => PluginState.Config));
        serviceCollection.AddSingleton(sp => new HealthService(
            sp.GetRequiredService<ILibraryAdapter>(),
            sp.GetRequiredService<JsonStore>(),
            sp.GetRequiredService<ActivityLog>(),
            sp.GetRequiredService<SafetyGuard>(),
            () => PluginState.Config));
        serviceCollection.AddSingleton(sp => new StorageService(
            sp.GetRequiredService<ILibraryAdapter>(),
            sp.GetRequiredService<IUserAdapter>(),
            sp.GetRequiredService<IDriveSpaceProvider>(),
            sp.GetRequiredService<JsonStore>(),
            sp.GetRequiredService<ActivityLog>(),
            () => PluginState.Config,
            () => sp.GetRequiredService<DuplicateService>().GetCached()));
        serviceCollection.AddSingleton(sp => new ImageService(
            sp.GetRequiredService<ILibraryAdapter>(),
            sp.GetRequiredService<IImageAdapter>(),
            sp.GetRequiredService<SafeFetcher>(),
            sp.GetRequiredService<FetchCache>(),
            () => Http(sp),
            sp.GetRequiredService<ActivityLog>(),
            sp.GetRequiredService<SafetyGuard>(),
            () => PluginState.Config));
        serviceCollection.AddSingleton(sp => new SettingsService(
            () => PluginState.Config,
            c => (Plugin.Instance ?? throw new ToolkitException("not_loaded", "The plugin is not loaded yet.", 503)).UpdateConfiguration(c),
            sp.GetRequiredService<ActivityLog>(),
            () => Http(sp)));
    }

    private static HttpClient Http(System.IServiceProvider sp) => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
}
