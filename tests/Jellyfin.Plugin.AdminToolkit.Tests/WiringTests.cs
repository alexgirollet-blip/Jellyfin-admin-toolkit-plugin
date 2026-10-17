using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.AdminToolkit.Api;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public sealed class WiringTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(PathsProxy.Create(Path.Combine(_tmp.Path, "data")));
        services.AddSingleton(ThrowingProxy.Create<ILibraryManager>());
        services.AddSingleton(ThrowingProxy.Create<IProviderManager>());
        services.AddSingleton(ThrowingProxy.Create<IFileSystem>());
        services.AddSingleton(ThrowingProxy.Create<IUserManager>());
        services.AddSingleton(ThrowingProxy.Create<IUserDataManager>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        new PluginServiceRegistrator().RegisterServices(services, null!);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void EveryControllerAndTheScheduledTask_Resolve_WithoutTouchingDisk()
    {
        using var sp = Build();
        var controllers = typeof(Plugin).Assembly.GetTypes().Where(t => typeof(ToolkitControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.Equal(7, controllers.Count);
        foreach (var c in controllers)
        {
            Assert.NotNull(ActivatorUtilities.CreateInstance(sp, c));
        }

        var task = ActivatorUtilities.CreateInstance<ScanTask>(sp);
        Assert.Empty(task.GetDefaultTriggers());
        Assert.False(Directory.Exists(Path.Combine(_tmp.Path, "data")), "nothing may be written at start-up / construction");
    }

    [Fact]
    public void EveryEndpoint_IsUnderAdminToolkit_AndRequiresElevation()
    {
        var controllers = typeof(Plugin).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.NotEmpty(controllers);
        foreach (var c in controllers)
        {
            var auth = c.GetCustomAttributes<AuthorizeAttribute>(true).ToList();
            Assert.Contains(auth, a => a.Policy == Policies.RequiresElevation);
            Assert.Empty(c.GetCustomAttributes<AllowAnonymousAttribute>(true));
            var route = c.GetCustomAttribute<RouteAttribute>()!.Template;
            Assert.StartsWith("AdminToolkit", route, StringComparison.Ordinal);
            foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.Empty(m.GetCustomAttributes<AllowAnonymousAttribute>(true));
            }
        }
    }

    [Fact]
    public void PluginPage_IsADashboardPageOnly()
    {
        var page = typeof(Plugin).GetMethod("GetPages")!;
        Assert.NotNull(page);
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();
        Assert.Contains("Jellyfin.Plugin.AdminToolkit.Web.adminToolkit.html", resources);
        Assert.DoesNotContain(typeof(Plugin).Assembly.GetTypes(), t => t.GetInterfaces().Any(i => i.Name == "IHostedService"));
    }
}

public class PathsProxy : DispatchProxy
{
    public string DataPath { get; set; } = string.Empty;

    public static IApplicationPaths Create(string dataPath)
    {
        var p = Create<IApplicationPaths, PathsProxy>();
        ((PathsProxy)(object)p).DataPath = dataPath;
        return p;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == "get_DataPath" ? DataPath : throw new InvalidOperationException(targetMethod.Name);
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Keys_AreMasked_MaskedValuesKeepSecrets_AndLogNeverContainsThem()
    {
        var stored = new PluginConfiguration { SonarrUrl = "http://sonarr:8989", SonarrApiKey = "abcdef123456", ImageSearchApiKey = "brave-secret-9999" };
        var log = new ActivityLog(_tmp.Path, () => 30);
        var svc = new SettingsService(() => stored, c => stored = c, log, () => new HttpClient());

        var masked = svc.GetMasked();
        Assert.Equal("••••3456", masked.SonarrApiKey);
        Assert.Equal("••••9999", masked.ImageSearchApiKey);
        Assert.Equal("abcdef123456", stored.SonarrApiKey);

        masked.SafeMode = false;
        masked.RadarrApiKey = "new-radarr-key";
        masked.DriveMappings.Add(new DriveMapping { Prefix = "/data/8tb1", Label = "8TB 1" });
        svc.Save(masked, "alex");

        Assert.Equal("abcdef123456", stored.SonarrApiKey);
        Assert.Equal("brave-secret-9999", stored.ImageSearchApiKey);
        Assert.Equal("new-radarr-key", stored.RadarrApiKey);
        Assert.False(stored.SafeMode);
        var entry = Assert.Single(log.Read(new ActivityFilter()));
        Assert.Contains("SafeMode: true -> false", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("RadarrApiKey changed", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("new-radarr-key", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SonarrApiKey", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetWeights_RestoresDefaults()
    {
        var stored = new PluginConfiguration { Weights = new ScoreWeights { ManagedPath = 1, Completeness = 2 } };
        var svc = new SettingsService(() => stored, c => stored = c, new ActivityLog(_tmp.Path, () => 30), () => new HttpClient());
        svc.ResetWeights("alex");
        Assert.Equal(100, stored.Weights.ManagedPath);
        Assert.Equal(60, stored.Weights.Completeness);
    }

    [Fact]
    public void WriteTest_OnlyAllowsConfiguredPaths()
    {
        var cfg = new PluginConfiguration { DriveMappings = { new DriveMapping { Prefix = "/data/8tb1", Label = "8TB 1" } } };
        Assert.True(SettingsService.IsTestablePath("/data/8tb1/", cfg, Array.Empty<string>()));
        Assert.True(SettingsService.IsTestablePath("/data/tv", cfg, new[] { "/data/tv" }));
        Assert.False(SettingsService.IsTestablePath("/etc", cfg, new[] { "/data/tv" }));
    }
}
