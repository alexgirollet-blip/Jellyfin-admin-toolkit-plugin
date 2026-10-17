using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Images;

/// <summary>
/// Decides which addresses server-side fetches of arbitrary URLs may connect to. Blocks loopback, private, link-local,
/// CGNAT, multicast, reserved and documentation ranges (IPv4 and IPv6, including IPv4-mapped/compatible and NAT64 forms),
/// so a URL can never make the Jellyfin server talk to itself, the LAN or a cloud metadata endpoint.
/// </summary>
public static class SsrfPolicy
{
    private static readonly (byte[] Net, int Bits)[] BlockedV4 =
    {
        (new byte[] { 0, 0, 0, 0 }, 8),          // "this" network
        (new byte[] { 10, 0, 0, 0 }, 8),         // private
        (new byte[] { 100, 64, 0, 0 }, 10),      // CGNAT
        (new byte[] { 127, 0, 0, 0 }, 8),        // loopback
        (new byte[] { 169, 254, 0, 0 }, 16),     // link-local (cloud metadata 169.254.169.254)
        (new byte[] { 172, 16, 0, 0 }, 12),      // private
        (new byte[] { 192, 0, 0, 0 }, 24),       // IETF protocol assignments
        (new byte[] { 192, 0, 2, 0 }, 24),       // documentation
        (new byte[] { 192, 88, 99, 0 }, 24),     // 6to4 relay
        (new byte[] { 192, 168, 0, 0 }, 16),     // private
        (new byte[] { 198, 18, 0, 0 }, 15),      // benchmarking
        (new byte[] { 198, 51, 100, 0 }, 24),    // documentation
        (new byte[] { 203, 0, 113, 0 }, 24),     // documentation
        (new byte[] { 224, 0, 0, 0 }, 4),        // multicast
        (new byte[] { 240, 0, 0, 0 }, 4)         // reserved + broadcast
    };

    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlocked(address.MapToIPv4());
            }

            var b = address.GetAddressBytes();
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                return true;
            }

            // IPv4-compatible ::a.b.c.d and NAT64 64:ff9b::a.b.c.d embed an IPv4 address.
            if (b.Take(12).All(x => x == 0) || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0)))
            {
                return IsBlocked(new IPAddress(b.Skip(12).ToArray()));
            }

            return (b[0] & 0xfe) == 0xfc           // fc00::/7 unique local
                   || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) // fe80::/10 link-local
                   || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0) // fec0::/10 site-local (deprecated)
                   || b[0] == 0xff                 // multicast
                   || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // 2001:db8::/32 documentation
                   || (b[0] == 0x20 && b[1] == 0x02); // 2002::/16 6to4 (can embed private IPv4)
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var a = address.GetAddressBytes();
        foreach (var (net, bits) in BlockedV4)
        {
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            uint ip = (uint)((a[0] << 24) | (a[1] << 16) | (a[2] << 8) | a[3]);
            uint n = (uint)((net[0] << 24) | (net[1] << 16) | (net[2] << 8) | net[3]);
            if ((ip & mask) == (n & mask))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Validates a URL before any network access: absolute, http/https, no credentials, sane port.</summary>
    public static Uri ValidateUrl(string? url, Func<IPAddress, bool>? isBlocked = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ToolkitException("bad_url", "Not a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ToolkitException("bad_url", $"Only http and https URLs can be fetched (got {uri.Scheme}:).");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ToolkitException("bad_url", "URLs with a user name or password are not fetched.");
        }

        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) && (isBlocked ?? IsBlocked)(literal))
        {
            throw new ToolkitException("blocked_address", $"{uri.Host} is a private, loopback or link-local address; the server does not fetch those.", 403);
        }

        return uri;
    }
}

/// <summary>Bytes fetched from a URL.</summary>
public sealed record FetchedImage(byte[] Data, string ContentType, Uri FinalUrl);

/// <summary>
/// Fetches images from arbitrary URLs safely. Every TCP connection, including each redirect hop (followed manually,
/// at most 3), resolves DNS itself and connects only to addresses <see cref="SsrfPolicy"/> allows, so DNS rebinding
/// and redirects to internal hosts are refused. Size (15 MB), time (15 s) and content type are limited.
/// </summary>
public sealed class SafeFetcher : IDisposable
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxRedirects = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _client;
    private readonly Func<IPAddress, bool> _isBlocked;

    public SafeFetcher()
        : this(SsrfPolicy.IsBlocked)
    {
    }

    /// <summary>Test hook: a different address policy (e.g. to allow a loopback test server).</summary>
    internal SafeFetcher(Func<IPAddress, bool> isBlocked)
    {
        _isBlocked = isBlocked;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync
        };
        _client = new HttpClient(handler) { Timeout = Timeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-AdminToolkit/1.0 (+image fetch)");
    }

    public void Dispose() => _client.Dispose();

    public async Task<FetchedImage> FetchAsync(string url, CancellationToken ct)
    {
        var uri = SsrfPolicy.ValidateUrl(url, _isBlocked);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.ParseAdd("image/png,image/webp,image/jpeg,image/gif;q=0.9,*/*;q=0.1");
                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is not null)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new ToolkitException("too_many_redirects", $"More than {MaxRedirects} redirects.");
                    }

                    var next = resp.Headers.Location.IsAbsoluteUri ? resp.Headers.Location : new Uri(uri, resp.Headers.Location);
                    uri = SsrfPolicy.ValidateUrl(next.ToString(), _isBlocked);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new ToolkitException("fetch_failed", $"The image server answered HTTP {(int)resp.StatusCode} for {uri.Host}.", 502);
                }

                var type = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!(type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || type is "application/octet-stream" or "binary/octet-stream" or ""))
                {
                    throw new ToolkitException("not_image", $"The URL returned \"{type}\", not an image.", 415);
                }

                if (resp.Content.Headers.ContentLength is > MaxBytes)
                {
                    throw new ToolkitException("too_large", $"The image is larger than {MaxBytes / 1024 / 1024} MB.", 413);
                }

                var data = await ReadLimitedAsync(resp, timeout.Token).ConfigureAwait(false);
                var sniffed = ImageInspector.SniffFormat(data);
                if (sniffed is null)
                {
                    throw new ToolkitException("not_image", "The downloaded file is not a PNG, JPEG, WebP or GIF image.", 415);
                }

                return new FetchedImage(data, ImageInspector.MimeFor(sniffed), uri);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ToolkitException("timeout", $"Fetching the image took longer than {Timeout.TotalSeconds:0} seconds.", 504);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolkitException("fetch_failed", "Could not fetch the image: " + (ex.InnerException?.Message ?? ex.Message), 502);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxBytes)
            {
                throw new ToolkitException("too_large", $"The image is larger than {MaxBytes / 1024 / 1024} MB.", 413);
            }

            await ms.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }

        return ms.ToArray();
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(a => !_isBlocked(a)).ToList();
        if (allowed.Count == 0)
        {
            throw new HttpRequestException($"{host} resolves only to private, loopback or link-local addresses ({string.Join(", ", addresses.Select(a => a.ToString()))}); refused.");
        }

        Exception? last = null;
        foreach (var addr in allowed)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addr, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException($"Could not connect to {host}.", last);
    }
}

/// <summary>Small in-memory cache of fetched images so analysing and previewing do not download twice.</summary>
public sealed class FetchCache
{
    private const long MaxTotal = 150L * 1024 * 1024;
    private readonly LinkedList<(string Url, FetchedImage Image)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, FetchedImage Image)>> _map = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _total;

    public bool TryGet(string url, out FetchedImage? image)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(url, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    public void Add(string url, FetchedImage image)
    {
        lock (_lock)
        {
            if (_map.ContainsKey(url))
            {
                return;
            }

            _map[url] = _order.AddFirst((url, image));
            _total += image.Data.Length;
            while (_total > MaxTotal && _order.Last is not null)
            {
                var last = _order.Last;
                _order.RemoveLast();
                _map.Remove(last.Value.Url);
                _total -= last.Value.Image.Data.Length;
            }
        }
    }
}
