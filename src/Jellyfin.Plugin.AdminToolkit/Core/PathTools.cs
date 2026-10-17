using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AdminToolkit.Configuration;

namespace Jellyfin.Plugin.AdminToolkit.Core;

/// <summary>Path comparison helpers. Paths are compared on whole segments ("/data/tv" does not contain "/data/tv2").</summary>
public static class PathText
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var p = path.Trim().Replace('\\', '/');
        while (p.Contains("//", StringComparison.Ordinal))
        {
            p = p.Replace("//", "/", StringComparison.Ordinal);
        }

        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="prefix"/> or is inside it.</summary>
    public static bool IsUnder(string? path, string? prefix, bool ignoreCase = false)
    {
        var p = Normalize(path);
        var pre = Normalize(prefix);
        if (p.Length == 0 || pre.Length == 0)
        {
            return false;
        }

        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (pre == "/")
        {
            return p.StartsWith('/');
        }

        return p.Equals(pre, cmp) || p.StartsWith(pre + "/", cmp);
    }

    public static bool SamePath(string? a, string? b) => Normalize(a).Equals(Normalize(b), StringComparison.Ordinal);

    /// <summary>Quotes a path for a POSIX shell (single quotes, embedded quotes escaped).</summary>
    public static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>Free/used space of the file system that contains a path.</summary>
public sealed record DriveSpace(long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>Reads disk space; replaced by a fake in tests.</summary>
public interface IDriveSpaceProvider
{
    /// <summary>Returns the space of the file system holding <paramref name="path"/>, or null when it cannot be read.</summary>
    DriveSpace? GetSpace(string path);
}

/// <summary>A labelled drive with its space (null when unreadable, e.g. the drive is not mounted).</summary>
public sealed class DriveView
{
    public string Label { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public bool Configured { get; set; }

    public long? TotalBytes { get; set; }

    public long? FreeBytes { get; set; }

    public long? UsedBytes { get; set; }

    public bool Exists { get; set; }

    public string? Problem { get; set; }
}

/// <summary>
/// Maps paths to the drive names configured in Settings (path prefix -> label). The longest matching prefix wins,
/// so "/data/8tb2/tv" can be labelled separately from "/data". Unmapped paths get null.
/// </summary>
public sealed class DriveMapper
{
    private readonly List<DriveMapping> _mappings;

    public DriveMapper(IEnumerable<DriveMapping> mappings)
    {
        _mappings = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.Prefix) && !string.IsNullOrWhiteSpace(m.Label))
            .OrderByDescending(m => PathText.Normalize(m.Prefix).Length)
            .ToList();
    }

    public IReadOnlyList<DriveMapping> Mappings => _mappings;

    public DriveMapping? Find(string? path) => _mappings.FirstOrDefault(m => PathText.IsUnder(path, m.Prefix));

    public string? LabelFor(string? path) => Find(path)?.Label;
}

/// <summary>
/// Translates Sonarr/Radarr paths to Jellyfin paths. Example: Sonarr sees "/tv/Show", Jellyfin sees "/data/tvshows/Show";
/// mapping ArrPrefix "/tv" -> JellyfinPrefix "/data/tvshows". Without a matching mapping the path is used unchanged.
/// </summary>
public sealed class ArrPathMapper
{
    private readonly List<PathMapping> _mappings;

    public ArrPathMapper(IEnumerable<PathMapping> mappings)
    {
        _mappings = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.ArrPrefix) && !string.IsNullOrWhiteSpace(m.JellyfinPrefix))
            .OrderByDescending(m => PathText.Normalize(m.ArrPrefix).Length)
            .ToList();
    }

    public string ToJellyfin(string arrPath)
    {
        var p = PathText.Normalize(arrPath);
        foreach (var m in _mappings)
        {
            var pre = PathText.Normalize(m.ArrPrefix);
            if (PathText.IsUnder(p, pre))
            {
                return PathText.Normalize(string.Concat(PathText.Normalize(m.JellyfinPrefix).AsSpan(), p.AsSpan(pre.Length)));
            }
        }

        return p;
    }
}

/// <summary>Masks secrets for responses. A masked value sent back unchanged keeps the stored secret.</summary>
public static class Secrets
{
    public const string MaskPrefix = "••••";

    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return secret.Length <= 4 ? MaskPrefix : MaskPrefix + secret[^4..];
    }

    public static bool IsMasked(string? value) => value is not null && value.StartsWith(MaskPrefix, StringComparison.Ordinal);

    /// <summary>Returns the value to store: the incoming one, or the existing one if the incoming value is the mask.</summary>
    public static string Merge(string? incoming, string existing) => IsMasked(incoming) ? existing : (incoming ?? string.Empty).Trim();
}
