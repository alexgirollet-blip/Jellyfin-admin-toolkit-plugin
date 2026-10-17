using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Images;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public class TransparencyTests
{
    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrulyTransparentPng_IsTransparent(bool skia)
    {
        var f = ImageInspector.Inspect(Fixture("logo-transparent.png"), skia);
        Assert.Null(f.Error);
        Assert.Equal(skia ? "skia" : "managed-png", f.Decoder);
        Assert.Equal("png", f.Format);
        Assert.Equal(400, f.Width);
        Assert.Equal(150, f.Height);
        Assert.True(f.HasAlphaChannel);
        Assert.Equal(Transparency.Transparent, f.Transparency);
        Assert.InRange(f.TransparentRatio, 0.5, 0.75);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpaquePngWithUnusedAlphaChannel_IsOpaque(bool skia)
    {
        var f = ImageInspector.Inspect(Fixture("logo-opaque-with-alpha.png"), skia);
        Assert.Null(f.Error);
        Assert.Equal(Transparency.Opaque, f.Transparency);
        Assert.Equal(0, f.TransparentRatio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Jpeg_IsOpaque_WithSize(bool skia)
    {
        var f = ImageInspector.Inspect(Fixture("poster.jpg"), skia);
        Assert.Null(f.Error);
        Assert.Equal("jpeg", f.Format);
        Assert.Equal("image/jpeg", f.MimeType);
        Assert.Equal(200, f.Width);
        Assert.Equal(300, f.Height);
        Assert.False(f.HasAlphaChannel);
        Assert.Equal(Transparency.Opaque, f.Transparency);
        Assert.Equal(0.667, f.AspectRatio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PalettePngWithTrns_IsTransparent(bool skia)
    {
        var f = ImageInspector.Inspect(Fixture("palette-transparent.png"), skia);
        Assert.Equal(Transparency.Transparent, f.Transparency);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterlacedPng_SingleSemiTransparentPixel_IsFound(bool skia)
    {
        var f = ImageInspector.Inspect(Fixture("interlaced-one-pixel.png"), skia);
        Assert.Equal(Transparency.Transparent, f.Transparency);
        Assert.Equal(Math.Round(1.0 / (33 * 17), 4), f.TransparentRatio);
    }

    [Theory]
    [InlineData("backdrop.png")]
    [InlineData("gray16.png")]
    public void PngWithoutAlpha_IsOpaque_InBothDecoders(string name)
    {
        Assert.Equal(Transparency.Opaque, ImageInspector.Inspect(Fixture(name), true).Transparency);
        Assert.Equal(Transparency.Opaque, ImageInspector.Inspect(Fixture(name), false).Transparency);
    }

    [Fact]
    public void TransparentWebp_IsTransparent_WithSkia()
    {
        var f = ImageInspector.Inspect(Fixture("logo-transparent.webp"));
        Assert.Equal("webp", f.Format);
        Assert.Equal(Transparency.Transparent, f.Transparency);
        Assert.Equal("This image type needs SkiaSharp, which could not be used.", ImageInspector.Inspect(Fixture("logo-transparent.webp"), false).Error);
    }

    [Fact]
    public void NotAnImage_IsRejected()
    {
        var f = ImageInspector.Inspect(Fixture("not-image.txt"));
        Assert.Equal(Transparency.Unknown, f.Transparency);
        Assert.NotNull(f.Error);
    }

    [Fact]
    public void TruncatedPng_DoesNotThrow()
    {
        var data = Fixture("logo-transparent.png");
        var cut = data.AsSpan(0, data.Length / 2).ToArray();
        var f = ImageInspector.Inspect(cut, false);
        Assert.Equal(Transparency.Unknown, f.Transparency);
        Assert.NotNull(f.Error);
    }
}

public class SsrfTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.5")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:101::1")]
    public void PrivateLoopbackLinkLocal_AreBlocked(string ip)
    {
        Assert.True(SsrfPolicy.IsBlocked(IPAddress.Parse(ip)), ip);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("151.101.1.1")]
    [InlineData("2606:4700::6810:84e5")]
    public void PublicAddresses_AreAllowed(string ip)
    {
        Assert.False(SsrfPolicy.IsBlocked(IPAddress.Parse(ip)), ip);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("gopher://example.com/")]
    [InlineData("http://user:pass@example.com/a.png")]
    [InlineData("not a url")]
    [InlineData("http://127.0.0.1/x.png")]
    [InlineData("http://[::1]/x.png")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void BadUrls_AreRefusedBeforeAnyRequest(string url)
    {
        Assert.Throws<ToolkitException>(() => SsrfPolicy.ValidateUrl(url));
    }

    [Fact]
    public async Task Localhost_ByName_IsRefusedAtConnectTime()
    {
        using var f = new SafeFetcher();
        var ex = await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync("http://localhost:9/x.png", CancellationToken.None));
        Assert.Equal("fetch_failed", ex.Code);
        Assert.Contains("refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectToPrivateAddress_IsRefused_EvenFromAnAllowedServer()
    {
        using var server = new MiniServer(path => path == "/start" ? MiniServer.Redirect("http://169.254.169.254/latest/meta-data/") : MiniServer.NotFound());
        using var f = new SafeFetcher(ip => !IPAddress.IsLoopback(ip) && SsrfPolicy.IsBlocked(ip));
        var ex = await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/start"), CancellationToken.None));
        Assert.Equal("blocked_address", ex.Code);
    }

    [Fact]
    public async Task FetchesImages_ChecksTypeAndSize()
    {
        var png = TransparencyTests.Fixture("logo-transparent.png");
        using var server = new MiniServer(path => path switch
        {
            "/logo.png" => MiniServer.Ok("image/png", png),
            "/hop" => MiniServer.Redirect("/logo.png"),
            "/html" => MiniServer.Ok("text/html", System.Text.Encoding.UTF8.GetBytes("<html></html>")),
            "/fake" => MiniServer.Ok("image/png", System.Text.Encoding.UTF8.GetBytes("this is not a png")),
            "/big" => MiniServer.Ok("image/png", new byte[SafeFetcher.MaxBytes + 10]),
            _ => MiniServer.NotFound()
        });
        using var f = new SafeFetcher(ip => !IPAddress.IsLoopback(ip) && SsrfPolicy.IsBlocked(ip));

        var ok = await f.FetchAsync(server.Url("/hop"), CancellationToken.None);
        Assert.Equal(png.Length, ok.Data.Length);
        Assert.Equal("image/png", ok.ContentType);
        Assert.EndsWith("/logo.png", ok.FinalUrl.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal("not_image", (await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/html"), CancellationToken.None))).Code);
        Assert.Equal("not_image", (await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/fake"), CancellationToken.None))).Code);
        Assert.Equal("too_large", (await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/big"), CancellationToken.None))).Code);
        Assert.Equal("fetch_failed", (await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/missing"), CancellationToken.None))).Code);
    }

    [Fact]
    public async Task DefaultPolicy_RefusesTheLoopbackTestServer()
    {
        using var server = new MiniServer(_ => MiniServer.Ok("image/png", TransparencyTests.Fixture("logo-transparent.png")));
        using var f = new SafeFetcher();
        await Assert.ThrowsAsync<ToolkitException>(() => f.FetchAsync(server.Url("/logo.png"), CancellationToken.None));
    }

    [Fact]
    public void FetchCache_EvictsOldest()
    {
        var c = new FetchCache();
        var img = new FetchedImage(new byte[60 * 1024 * 1024], "image/png", new Uri("https://x/"));
        c.Add("a", img);
        c.Add("b", img);
        c.Add("c", img);
        Assert.False(c.TryGet("a", out _));
        Assert.True(c.TryGet("c", out var hit));
        Assert.Same(img, hit);
    }
}

/// <summary>Tiny HTTP/1.1 server on 127.0.0.1 for fetch tests.</summary>
public sealed class MiniServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public MiniServer(Func<string, byte[]> respond)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var s = client.GetStream();
                        var reader = new StreamReader(s);
                        var line = await reader.ReadLineAsync() ?? string.Empty;
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                        {
                        }

                        var path = line.Split(' ').Length > 1 ? line.Split(' ')[1] : "/";
                        var bytes = respond(path);
                        await s.WriteAsync(bytes);
                        await s.FlushAsync();
                    }
                });
            }
        });
    }

    public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

    public static byte[] Ok(string type, byte[] body)
    {
        var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all, head.Length);
        return all;
    }

    public static byte[] Redirect(string location)
        => System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    public static byte[] NotFound()
        => System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
