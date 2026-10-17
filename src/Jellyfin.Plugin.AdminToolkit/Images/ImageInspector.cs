using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Jellyfin.Plugin.AdminToolkit.Core;
using SkiaSharp;

namespace Jellyfin.Plugin.AdminToolkit.Images;

public enum Transparency
{
    /// <summary>At least one pixel has alpha below 250: the background really is (partly) see-through.</summary>
    Transparent,

    /// <summary>No alpha channel, or an alpha channel in which every pixel is fully opaque.</summary>
    Opaque,

    /// <summary>Could not be decoded.</summary>
    Unknown
}

/// <summary>What the inspector found in an image.</summary>
public sealed class ImageFacts
{
    public string Format { get; set; } = "unknown";

    public string MimeType { get; set; } = "application/octet-stream";

    public int Width { get; set; }

    public int Height { get; set; }

    public long Bytes { get; set; }

    public bool HasAlphaChannel { get; set; }

    public Transparency Transparency { get; set; } = Transparency.Unknown;

    /// <summary>Gets or sets the share of pixels with alpha below 250 (0..1).</summary>
    public double TransparentRatio { get; set; }

    /// <summary>Gets or sets "skia" or "managed-png" (which decoder answered).</summary>
    public string Decoder { get; set; } = string.Empty;

    public string? Error { get; set; }

    public double AspectRatio => Height == 0 ? 0 : Math.Round((double)Width / Height, 3);
}

/// <summary>
/// Decodes images to find their real format, size and whether they are actually transparent. Uses SkiaSharp (the copy
/// the Jellyfin 10.11.10 server ships) and falls back to a small managed PNG decoder if SkiaSharp cannot be loaded.
/// A PNG/WebP with an alpha channel whose pixels are all opaque is reported as Opaque.
/// </summary>
public static class ImageInspector
{
    /// <summary>Images larger than this are not decoded (decompression-bomb protection).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>Alpha values at or above this count as opaque (ignores near-opaque encoder noise).</summary>
    public const byte OpaqueAlpha = 250;

    private static bool _skiaUnavailable;

    /// <summary>Format from magic bytes: png, jpeg, webp, gif, or null.</summary>
    public static string? SniffFormat(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return "png";
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "jpeg";
        }

        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            return "webp";
        }

        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8')
        {
            return "gif";
        }

        return null;
    }

    public static string MimeFor(string format) => format switch
    {
        "png" => "image/png",
        "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        _ => "application/octet-stream"
    };

    public static ImageFacts Inspect(byte[] data, bool allowSkia = true)
    {
        var facts = new ImageFacts { Bytes = data.Length };
        var format = SniffFormat(data);
        if (format is null)
        {
            facts.Error = "Not a PNG, JPEG, WebP or GIF image.";
            return facts;
        }

        facts.Format = format;
        facts.MimeType = MimeFor(format);
        if (allowSkia && !_skiaUnavailable)
        {
            try
            {
                if (InspectWithSkia(data, facts))
                {
                    facts.Decoder = "skia";
                    return facts;
                }
            }
            catch (Exception ex) when (ErrorText.IsVersionMismatch(ex) || ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
            {
                _skiaUnavailable = true;
            }
        }

        try
        {
            if (format == "png")
            {
                ManagedPng.Inspect(data, facts);
                facts.Decoder = "managed-png";
            }
            else if (format == "jpeg")
            {
                JpegSize(data, facts);
                facts.HasAlphaChannel = false;
                facts.Transparency = facts.Width > 0 ? Transparency.Opaque : Transparency.Unknown;
                facts.Decoder = "managed-jpeg";
            }
            else
            {
                facts.Error ??= "This image type needs SkiaSharp, which could not be used.";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            facts.Error = "Could not decode the image: " + ex.Message;
            facts.Transparency = Transparency.Unknown;
        }

        return facts;
    }

    private static bool InspectWithSkia(byte[] data, ImageFacts facts)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null)
        {
            facts.Error = "SkiaSharp could not read the image.";
            return false;
        }

        var info = codec.Info;
        facts.Width = info.Width;
        facts.Height = info.Height;
        if ((long)info.Width * info.Height > MaxPixels)
        {
            facts.Error = $"Image is {info.Width}x{info.Height}; larger than {MaxPixels / 1_000_000} megapixels is not decoded.";
            facts.Transparency = Transparency.Unknown;
            facts.HasAlphaChannel = info.AlphaType != SKAlphaType.Opaque;
            return true;
        }

        if (info.AlphaType == SKAlphaType.Opaque)
        {
            facts.HasAlphaChannel = false;
            facts.Transparency = Transparency.Opaque;
            return true;
        }

        facts.HasAlphaChannel = true;
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(decodeInfo);
        var r = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            facts.Error = "SkiaSharp could not decode the pixels: " + r;
            return false;
        }

        var span = bitmap.GetPixelSpan();
        long transparent = 0;
        for (int i = 3; i < span.Length; i += 4)
        {
            if (span[i] < OpaqueAlpha)
            {
                transparent++;
            }
        }

        long total = (long)info.Width * info.Height;
        facts.TransparentRatio = total == 0 ? 0 : Math.Round((double)transparent / total, 4);
        facts.Transparency = transparent > 0 ? Transparency.Transparent : Transparency.Opaque;
        return true;
    }

    private static void JpegSize(byte[] d, ImageFacts facts)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
            {
                i++;
                continue;
            }

            byte marker = d[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                facts.Height = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 5));
                facts.Width = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 7));
                return;
            }

            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
        }
    }
}

/// <summary>
/// Minimal PNG decoder used only to measure transparency when SkiaSharp is unavailable. Supports every PNG colour type
/// and bit depth, tRNS, and Adam7 interlacing. Never allocates more than the decompressed image data.
/// </summary>
internal static class ManagedPng
{
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7 =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)
    };

    public static void Inspect(byte[] d, ImageFacts facts)
    {
        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            if (len < 0 || pos + 12 + len > d.Length)
            {
                throw new InvalidDataException("truncated PNG chunk " + type);
            }

            var body = d.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }

            pos += 12 + len;
            if (type == "IEND")
            {
                break;
            }
        }

        facts.Width = width;
        facts.Height = height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG has no valid IHDR");
        }

        if ((long)width * height > ImageInspector.MaxPixels)
        {
            facts.Error = $"Image is {width}x{height}; too large to decode.";
            return;
        }

        bool alphaChannel = colorType is 4 or 6;
        facts.HasAlphaChannel = alphaChannel || trns is not null;
        if (!facts.HasAlphaChannel)
        {
            facts.Transparency = Transparency.Opaque;
            return;
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("bad colour type") };
        int bitsPerPixel = channels * bitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8);

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        long transparent = 0;
        var passes = interlace == 1 ? Adam7 : new[] { (0, 0, 1, 1) };
        foreach (var (x0, y0, dx, dy) in passes)
        {
            int pw = (width - x0 + dx - 1) / dx;
            int ph = (height - y0 + dy - 1) / dy;
            if (pw <= 0 || ph <= 0)
            {
                continue;
            }

            int stride = (int)(((long)pw * bitsPerPixel + 7) / 8);
            var prev = new byte[stride];
            var cur = new byte[stride];
            for (int y = 0; y < ph; y++)
            {
                int filter = z.ReadByte();
                if (filter < 0)
                {
                    throw new InvalidDataException("PNG data ended early");
                }

                z.ReadExactly(cur);
                Unfilter(filter, cur, prev, bpp);
                transparent += CountTransparent(cur, pw, colorType, bitDepth, trns);
                (prev, cur) = (cur, prev);
            }
        }

        long total = (long)width * height;
        facts.TransparentRatio = Math.Round((double)transparent / total, 4);
        facts.Transparency = transparent > 0 ? Transparency.Transparent : Transparency.Opaque;
    }

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        for (int i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            cur[i] = filter switch
            {
                0 => cur[i],
                1 => (byte)(cur[i] + a),
                2 => (byte)(cur[i] + b),
                3 => (byte)(cur[i] + ((a + b) >> 1)),
                4 => (byte)(cur[i] + Paeth(a, b, c)),
                _ => throw new InvalidDataException("bad PNG filter " + filter)
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int Sample(byte[] row, int index, int bitDepth)
    {
        switch (bitDepth)
        {
            case 16:
                return (row[index * 2] << 8) | row[(index * 2) + 1];
            case 8:
                return row[index];
            default:
                int bit = index * bitDepth;
                int shift = 8 - bitDepth - (bit % 8);
                return (row[bit / 8] >> shift) & ((1 << bitDepth) - 1);
        }
    }

    private static long CountTransparent(byte[] row, int pixels, int colorType, int bitDepth, byte[]? trns)
    {
        long n = 0;
        int max = (1 << bitDepth) - 1;
        double threshold = ImageInspector.OpaqueAlpha / 255.0 * max;
        for (int x = 0; x < pixels; x++)
        {
            switch (colorType)
            {
                case 6:
                    if (Sample(row, (x * 4) + 3, bitDepth) < threshold)
                    {
                        n++;
                    }

                    break;
                case 4:
                    if (Sample(row, (x * 2) + 1, bitDepth) < threshold)
                    {
                        n++;
                    }

                    break;
                case 3:
                    int idx = Sample(row, x, bitDepth);
                    if (trns is not null && idx < trns.Length && trns[idx] < ImageInspector.OpaqueAlpha)
                    {
                        n++;
                    }

                    break;
                case 0:
                    if (trns is { Length: >= 2 } && Sample(row, x, bitDepth) == ((trns[0] << 8) | trns[1]))
                    {
                        n++;
                    }

                    break;
                case 2:
                    if (trns is { Length: >= 6 }
                        && Sample(row, x * 3, bitDepth) == ((trns[0] << 8) | trns[1])
                        && Sample(row, (x * 3) + 1, bitDepth) == ((trns[2] << 8) | trns[3])
                        && Sample(row, (x * 3) + 2, bitDepth) == ((trns[4] << 8) | trns[5]))
                    {
                        n++;
                    }

                    break;
            }
        }

        return n;
    }
}
