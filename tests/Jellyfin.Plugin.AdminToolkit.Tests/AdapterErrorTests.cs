using System;
using System.Reflection;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Api;
using Jellyfin.Plugin.AdminToolkit.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

/// <summary>Proxy that throws the given exception from every interface member (simulates a version break).</summary>
public class ThrowingProxy : DispatchProxy
{
    public Func<MethodInfo, Exception> Make { get; set; } = m => new MissingMethodException("Method not found: '" + m.Name + "'.");

    public static T Create<T>(Func<MethodInfo, Exception>? make = null)
        where T : class
    {
        var proxy = Create<T, ThrowingProxy>();
        if (make is not null)
        {
            ((ThrowingProxy)(object)proxy).Make = make;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw Make(targetMethod!);
}

public class AdapterErrorTests
{
    [Fact]
    public void MissingMethod_IsReportedAsReadableVersionMismatch()
    {
        var ex = Assert.Throws<JellyfinApiException>(() =>
            JellyfinCall.Run<int>("list users", () => throw new MissingMethodException("Method not found: 'System.Collections.Generic.IEnumerable`1 MediaBrowser.Controller.Library.IUserManager.get_Users()'.")));

        Assert.True(ex.IsVersionMismatch);
        Assert.StartsWith("Jellyfin could not list users:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("made for Jellyfin 10.11.10", ex.Message, StringComparison.Ordinal);
        Assert.Contains("get_Users", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeLoad_IsVersionMismatch()
    {
        var ex = Assert.Throws<JellyfinApiException>(() => JellyfinCall.Run("read streams", () => throw new TypeLoadException("Could not load type 'MediaStream'")));
        Assert.True(ex.IsVersionMismatch);
    }

    [Fact]
    public void OtherErrors_KeepTheirMessage_AndAreNotVersionMismatch()
    {
        var ex = Assert.Throws<JellyfinApiException>(() => JellyfinCall.Run<int>("look up an item", () => throw new InvalidOperationException("database is locked")));
        Assert.False(ex.IsVersionMismatch);
        Assert.Equal("Jellyfin could not look up an item: InvalidOperationException: database is locked", ex.Message);
    }

    [Fact]
    public void ToolkitAndCancellation_PassThroughUnwrapped()
    {
        Assert.Throws<ToolkitException>(() => JellyfinCall.Run<int>("x", () => throw new ToolkitException("safe_mode", "Safe mode is on")));
        Assert.Throws<OperationCanceledException>(() => JellyfinCall.Run<int>("x", () => throw new OperationCanceledException()));
    }

    [Fact]
    public void LibraryAdapter_WrapsVersionBreakFromILibraryManager()
    {
        var adapter = new JellyfinLibraryAdapter(ThrowingProxy.Create<ILibraryManager>(), ThrowingProxy.Create<IProviderManager>(), ThrowingProxy.Create<IFileSystem>());
        var ex = Assert.Throws<JellyfinApiException>(() => adapter.GetLibraries());
        Assert.True(ex.IsVersionMismatch);
        Assert.Contains("list the libraries", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetVirtualFolders", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAdapter_WrapsVersionBreakFromIUserManager()
    {
        var adapter = new JellyfinUserAdapter(ThrowingProxy.Create<IUserManager>(), ThrowingProxy.Create<IUserDataManager>(), ThrowingProxy.Create<ILibraryManager>());
        var ex = Assert.Throws<JellyfinApiException>(() => adapter.GetUsers());
        Assert.True(ex.IsVersionMismatch);
        Assert.Contains("list users", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageAdapter_RuntimeErrorFromProviderManager_IsReadable()
    {
        var adapter = new JellyfinImageAdapter(ThrowingProxy.Create<ILibraryManager>(_ => new InvalidOperationException("item table missing")), ThrowingProxy.Create<IProviderManager>());
        var ex = Assert.Throws<JellyfinApiException>(() => adapter.GetImages(Guid.NewGuid()));
        Assert.False(ex.IsVersionMismatch);
        Assert.Contains("item table missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownImageType_IsRejectedClearly()
    {
        var ex = Assert.Throws<ToolkitException>(() => JellyfinImageAdapter.ParseType("Poster"));
        Assert.Equal("bad_image_type", ex.Code);
        Assert.Equal(MediaBrowser.Model.Entities.ImageType.Logo, JellyfinImageAdapter.ParseType("logo"));
    }

    [Fact]
    public void ErrorFilter_ProducesJsonWithMessageEndpointAndStatus()
    {
        var inner = new JellyfinApiException("Jellyfin could not list users: boom", new MissingMethodException("x"));
        var result = ErrorResponse.Build(inner, "GET", "/AdminToolkit/Storage");
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("\"error\":\"jellyfin_version_mismatch\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("Jellyfin could not list users: boom", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":\"GET /AdminToolkit/Storage\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"status\":500", result.Content, StringComparison.Ordinal);

        var refusal = ErrorResponse.Build(new ToolkitException("safe_mode", "Safe mode is on.", 403), "POST", "/AdminToolkit/Duplicates/Execute");
        Assert.Equal(403, refusal.StatusCode);
        Assert.Contains("\"error\":\"safe_mode\"", refusal.Content, StringComparison.Ordinal);
    }
}
