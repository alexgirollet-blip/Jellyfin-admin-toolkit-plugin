using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Images;
using Jellyfin.Plugin.AdminToolkit.Integrations.ImageSearch;
using Jellyfin.Plugin.AdminToolkit.Storage;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>An item in the Images tab lists.</summary>
public sealed class ImageItemSummary
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string? SeriesName { get; set; }

    public string? LibraryName { get; set; }

    public Dictionary<string, int> ImageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An image candidate from any source.</summary>
public sealed class ImageCandidate
{
    public string Url { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    /// <summary>Gets or sets "Jellyfin" (remote providers) or "Web".</summary>
    public string Source { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Language { get; set; }

    public string? Title { get; set; }

    public string? SourcePage { get; set; }

    public double? CommunityRating { get; set; }
}

/// <summary>Server-side analysis of one candidate URL.</summary>
public sealed class ImageAnalysis
{
    public string Url { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long Bytes { get; set; }

    public string? Format { get; set; }

    public string Transparency { get; set; } = "Unknown";

    public double TransparentRatio { get; set; }

    public double AspectRatio { get; set; }

    public List<string> Warnings { get; set; } = new();
}

/// <summary>Set, add or replace an image.</summary>
public sealed class ApplyImageRequest
{
    public string Type { get; set; } = "Primary";

    public string? Url { get; set; }

    /// <summary>Gets or sets a browser upload as base64 (max 15 MB decoded).</summary>
    public string? DataBase64 { get; set; }

    /// <summary>Gets or sets "replace" (set/replace the image at Index) or "add" (add as an extra backdrop).</summary>
    public string Mode { get; set; } = "replace";

    public int? Index { get; set; }

    public bool Lock { get; set; }

    public bool Confirm { get; set; }
}

/// <summary>The Images tab: current artwork, candidates from Jellyfin's providers and the web, transparency, and changes.</summary>
public sealed class ImageService
{
    public static readonly string[] EditableTypes = { "Primary", "Backdrop", "Logo", "Thumb", "Banner", "Art", "Disc" };

    private readonly ILibraryAdapter _library;
    private readonly IImageAdapter _images;
    private readonly SafeFetcher _fetcher;
    private readonly FetchCache _cache;
    private readonly Func<HttpClient> _http;
    private readonly ActivityLog _log;
    private readonly SafetyGuard _guard;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<byte[], ImageFacts> _inspect;

    public ImageService(ILibraryAdapter library, IImageAdapter images, SafeFetcher fetcher, FetchCache cache, Func<HttpClient> http, ActivityLog log, SafetyGuard guard, Func<PluginConfiguration> config, Func<byte[], ImageFacts>? inspect = null)
    {
        _library = library;
        _images = images;
        _fetcher = fetcher;
        _cache = cache;
        _http = http;
        _log = log;
        _guard = guard;
        _config = config;
        _inspect = inspect ?? (b => ImageInspector.Inspect(b));
    }

    /// <summary>Warnings when an image does not suit the chosen image type (aspect ratio, size, transparency).</summary>
    public static List<string> Suitability(string type, MediaKind kind, int width, int height, Transparency transparency)
    {
        var w = new List<string>();
        if (width <= 0 || height <= 0)
        {
            return w;
        }

        double ar = (double)width / height;
        string Ratio() => ar.ToString("0.00", CultureInfo.InvariantCulture);
        switch (type)
        {
            case "Primary" when kind == MediaKind.Episode:
                if (ar < 1.5 || ar > 1.9)
                {
                    w.Add($"Episode images are usually 16:9 (1.78); this one is {Ratio()}.");
                }

                break;
            case "Primary":
                if (ar < 0.6 || ar > 0.75)
                {
                    w.Add($"Posters should be about 2:3 (0.67); this one is {Ratio()}.");
                }

                if (height < 900)
                {
                    w.Add(string.Create(CultureInfo.InvariantCulture, $"Low resolution for a poster ({width}x{height}); 1000x1500 or more looks sharper."));
                }

                break;
            case "Backdrop":
            case "Thumb":
                if (ar < 1.6 || ar > 1.95)
                {
                    w.Add($"{type} images should be 16:9 (1.78); this one is {Ratio()}.");
                }

                if (type == "Backdrop" && width < 1280)
                {
                    w.Add(string.Create(CultureInfo.InvariantCulture, $"Low resolution for a backdrop ({width}x{height}); 1920x1080 or more is better."));
                }

                break;
            case "Banner":
                if (ar < 4.5 || ar > 6.5)
                {
                    w.Add($"Banners are very wide (about 5.4:1, e.g. 1000x185); this one is {Ratio()}.");
                }

                break;
            case "Logo":
                if (ar < 1.5)
                {
                    w.Add($"Logos are normally wide (2:1 to 4:1); this one is {Ratio()}.");
                }

                break;
            case "Disc":
                if (ar < 0.9 || ar > 1.1)
                {
                    w.Add($"Disc art should be square; this one is {Ratio()}.");
                }

                break;
        }

        if (type is "Logo" or "Art" or "Disc" && transparency == Transparency.Opaque)
        {
            w.Add($"This image has no transparent background; {type} images are drawn over artwork and normally need transparency.");
        }

        return w;
    }

    public static string DefaultQuery(ItemSnapshot item, string type)
    {
        string title = item.Kind switch
        {
            MediaKind.Season => $"{item.SeriesName} season {item.SeasonNumber}",
            MediaKind.Episode => $"{item.SeriesName} {item.Name}",
            _ => item.Year is null ? item.Name : $"{item.Name} {item.Year}"
        };
        string word = type switch
        {
            "Primary" => item.Kind == MediaKind.Person ? "photo" : "poster",
            "Backdrop" => "backdrop wallpaper",
            "Logo" => "logo transparent png",
            "Thumb" => "thumbnail",
            "Banner" => "banner",
            "Art" => "clearart transparent png",
            "Disc" => "disc art transparent png",
            _ => type.ToLowerInvariant()
        };
        return (title + " " + word).Trim();
    }

    public IReadOnlyList<ImageItemSummary> Search(string query, int limit = 60)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ImageItemSummary>();
        }

        return _library.GetItems(new ItemQuery
        {
            Kinds = new[] { MediaKind.Movie, MediaKind.Series, MediaKind.Season, MediaKind.Episode, MediaKind.BoxSet, MediaKind.Person },
            NameContains = query,
            Limit = Math.Clamp(limit, 1, 200),
            WithStreams = false
        }).Select(Summary).ToList();
    }

    public IReadOnlyList<ImageItemSummary> Browse(Guid libraryId, int limit = 500)
        => _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Movie, MediaKind.Series, MediaKind.BoxSet }, LibraryId = libraryId, Limit = Math.Clamp(limit, 1, 5000), WithStreams = false })
            .Select(Summary).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Items that have no image of <paramref name="type"/> (bulk mode).</summary>
    public IReadOnlyList<ImageItemSummary> Missing(string type, IReadOnlyList<MediaKind> kinds, Guid? libraryId, int limit = 1000)
    {
        JellyfinImageAdapter.ParseType(type);
        return _library.GetItems(new ItemQuery { Kinds = kinds.Count == 0 ? new[] { MediaKind.Movie, MediaKind.Series } : kinds, LibraryId = libraryId, WithStreams = false })
            .Where(i => i.Images(type) == 0)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 10000))
            .Select(Summary)
            .ToList();
    }

    public object GetItem(Guid id)
    {
        var item = _library.GetItem(id, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        var images = _images.GetImages(id);
        IReadOnlyList<string> providers;
        string? providerError = null;
        try
        {
            providers = _images.GetRemoteProviders(id);
        }
        catch (JellyfinApiException ex)
        {
            providers = Array.Empty<string>();
            providerError = ex.Message;
        }

        return new
        {
            item = Summary(item),
            path = item.Path,
            locked = _images.IsLocked(id),
            images,
            remoteProviders = providers,
            remoteProvidersError = providerError,
            types = EditableTypes,
            defaultQueries = EditableTypes.ToDictionary(t => t, t => DefaultQuery(item, t)),
            webSearchConfigured = ImageSearchFactory.Create(_config(), _http()) is not null
        };
    }

    public async Task<IReadOnlyList<ImageCandidate>> GetRemoteAsync(Guid id, string? type, bool allLanguages, CancellationToken ct)
    {
        var list = await _images.GetRemoteImagesAsync(id, type, allLanguages, ct).ConfigureAwait(false);
        return list.Select(r => new ImageCandidate
        {
            Url = r.Url,
            ThumbnailUrl = r.ThumbnailUrl,
            Source = "Jellyfin",
            Provider = r.Provider,
            Width = r.Width,
            Height = r.Height,
            Language = r.Language,
            CommunityRating = r.CommunityRating
        }).ToList();
    }

    public async Task<object> SearchWebAsync(Guid id, string type, string? query, CancellationToken ct)
    {
        var item = _library.GetItem(id, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        var q = string.IsNullOrWhiteSpace(query) ? DefaultQuery(item, type) : query.Trim();
        var provider = ImageSearchFactory.Create(_config(), _http());
        if (provider is null)
        {
            return new { configured = false, help = ImageSearchFactory.SetupHelp, query = q, results = Array.Empty<ImageCandidate>() };
        }

        var results = await provider.SearchAsync(q, 40, ct).ConfigureAwait(false);
        return new
        {
            configured = true,
            provider = provider.Name,
            query = q,
            results = results.Select(r => new ImageCandidate
            {
                Url = r.Url,
                ThumbnailUrl = r.ThumbnailUrl,
                Source = "Web",
                Provider = r.Provider,
                Width = r.Width,
                Height = r.Height,
                Title = r.Title,
                SourcePage = r.SourcePage
            }).ToList()
        };
    }

    /// <summary>Downloads (SSRF-safe, cached) and decodes each URL; at most 4 at a time.</summary>
    public async Task<IReadOnlyList<ImageAnalysis>> AnalyzeAsync(IReadOnlyList<string> urls, string type, MediaKind kind, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = urls.Distinct(StringComparer.Ordinal).Take(60).Select(async url =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var img = await FetchAsync(url, ct).ConfigureAwait(false);
                return Analyse(url, img.Data, type, kind);
            }
            catch (ToolkitException ex)
            {
                return new ImageAnalysis { Url = url, Ok = false, Error = ex.Message };
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task<FetchedImage> FetchAsync(string url, CancellationToken ct)
        => FetchCachedAsync(url, ct);

    public async Task<object> ApplyAsync(Guid itemId, ApplyImageRequest req, string user, CancellationToken ct)
    {
        _guard.EnsureChangesAllowed("change artwork");
        if (!req.Confirm)
        {
            throw new ToolkitException("confirmation_required", "Confirm the change (before/after preview) first.", 409);
        }

        var item = _library.GetItem(itemId, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        var type = JellyfinImageAdapter.ParseType(req.Type).ToString();
        byte[] data;
        string source;
        if (!string.IsNullOrWhiteSpace(req.DataBase64))
        {
            var b64 = req.DataBase64.Contains(',', StringComparison.Ordinal) ? req.DataBase64[(req.DataBase64.IndexOf(',', StringComparison.Ordinal) + 1)..] : req.DataBase64;
            if (b64.Length > (SafeFetcher.MaxBytes / 3 * 4) + 8)
            {
                throw new ToolkitException("too_large", $"Uploads are limited to {SafeFetcher.MaxBytes / 1024 / 1024} MB.", 413);
            }

            try
            {
                data = Convert.FromBase64String(b64);
            }
            catch (FormatException)
            {
                throw new ToolkitException("bad_upload", "The uploaded file could not be read.");
            }

            source = "upload";
        }
        else
        {
            data = (await FetchCachedAsync(req.Url ?? string.Empty, ct).ConfigureAwait(false)).Data;
            source = req.Url!;
        }

        var facts = _inspect(data);
        if (facts.Error is not null || facts.Width == 0)
        {
            throw new ToolkitException("bad_image", "The image cannot be used: " + (facts.Error ?? "could not read its size."), 415);
        }

        var before = _images.GetImages(itemId).Where(i => i.Type == type).ToList();
        int? index = null;
        bool add = string.Equals(req.Mode, "add", StringComparison.OrdinalIgnoreCase);
        if (add && type != "Backdrop")
        {
            throw new ToolkitException("bad_mode", "Only backdrops can have more than one image; use replace.");
        }

        if (!add && type == "Backdrop" && before.Count > 0)
        {
            index = Math.Clamp(req.Index ?? 0, 0, before.Count - 1);
        }

        using (var ms = new MemoryStream(data, false))
        {
            await _images.SaveImageAsync(itemId, ms, facts.MimeType, type, index, ct).ConfigureAwait(false);
        }

        if (req.Lock && !_images.IsLocked(itemId))
        {
            await _images.SetLockedAsync(itemId, true, ct).ConfigureAwait(false);
        }

        var warnings = Suitability(type, item.Kind, facts.Width, facts.Height, facts.Transparency);
        var prev = before.FirstOrDefault(b => b.Index == (index ?? 0));
        var detail = string.Create(CultureInfo.InvariantCulture, $"{(add ? "Added" : before.Count == 0 ? "Set" : "Replaced")} {type}{(index is null ? string.Empty : " #" + index)}: {facts.Width}x{facts.Height} {facts.Format}, {facts.Transparency}, {facts.Bytes / 1024} KB from {source}.")
                     + (prev is null || add ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" Before: {prev.Width}x{prev.Height} {prev.Path}."))
                     + (req.Lock ? " Item locked." : string.Empty);
        _log.Add(user, "Images", add ? "Add image" : "Set image", Title(item), "OK", detail);
        return new { ok = true, message = detail, warnings, images = _images.GetImages(itemId) };
    }

    public async Task<object> DeleteAsync(Guid itemId, string type, int index, string user, CancellationToken ct)
    {
        _guard.EnsureChangesAllowed("delete artwork");
        var item = _library.GetItem(itemId, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        var t = JellyfinImageAdapter.ParseType(type).ToString();
        var slot = _images.GetImages(itemId).FirstOrDefault(i => i.Type == t && i.Index == index)
                   ?? throw new ToolkitException("not_found", $"The item has no {t} image #{index}.", 404);
        await _images.DeleteImageAsync(itemId, t, index, ct).ConfigureAwait(false);
        _log.Add(user, "Images", "Delete image", Title(item), "OK", string.Create(CultureInfo.InvariantCulture, $"Deleted {t} #{index} ({slot.Width}x{slot.Height}, {slot.Path})."));
        return new { ok = true, images = _images.GetImages(itemId) };
    }

    /// <summary>Moves a backdrop from one position to another with adjacent swaps (Jellyfin only swaps pairs).</summary>
    public async Task<object> ReorderAsync(Guid itemId, string type, int from, int to, string user, CancellationToken ct)
    {
        _guard.EnsureChangesAllowed("reorder artwork");
        var item = _library.GetItem(itemId, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        var t = JellyfinImageAdapter.ParseType(type).ToString();
        int count = _images.GetImages(itemId).Count(i => i.Type == t);
        if (from < 0 || to < 0 || from >= count || to >= count)
        {
            throw new ToolkitException("bad_index", string.Create(CultureInfo.InvariantCulture, $"Positions must be between 0 and {count - 1}."));
        }

        int step = to > from ? 1 : -1;
        for (int i = from; i != to; i += step)
        {
            await _images.SwapImagesAsync(itemId, t, i, i + step, ct).ConfigureAwait(false);
        }

        _log.Add(user, "Images", "Reorder images", Title(item), "OK", string.Create(CultureInfo.InvariantCulture, $"Moved {t} #{from} to #{to}."));
        return new { ok = true, images = _images.GetImages(itemId) };
    }

    public async Task<object> LockAsync(Guid itemId, bool locked, string user, CancellationToken ct)
    {
        _guard.EnsureChangesAllowed("lock or unlock an item");
        var item = _library.GetItem(itemId, false) ?? throw new ToolkitException("not_found", "Item not found.", 404);
        await _images.SetLockedAsync(itemId, locked, ct).ConfigureAwait(false);
        _log.Add(user, "Images", locked ? "Lock item" : "Unlock item", Title(item), "OK", locked
            ? "Item locked: Jellyfin's metadata refreshes will not change it (Jellyfin has no per-image lock)."
            : "Item unlocked.");
        return new { ok = true, locked };
    }

    private static string Title(ItemSnapshot i) => i.Kind switch
    {
        MediaKind.Episode or MediaKind.Season => $"{i.SeriesName}: {i.Name}",
        _ => i.Year is null ? i.Name : $"{i.Name} ({i.Year})"
    };

    private static ImageItemSummary Summary(ItemSnapshot i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Year = i.Year,
        Kind = i.Kind,
        SeriesName = i.SeriesName,
        LibraryName = i.LibraryName,
        ImageCounts = i.ImageCounts
    };

    private ImageAnalysis Analyse(string url, byte[] data, string type, MediaKind kind)
    {
        var f = _inspect(data);
        var a = new ImageAnalysis
        {
            Url = url,
            Ok = f.Error is null,
            Error = f.Error,
            Width = f.Width,
            Height = f.Height,
            Bytes = f.Bytes,
            Format = f.Format,
            Transparency = f.Transparency.ToString(),
            TransparentRatio = f.TransparentRatio,
            AspectRatio = f.AspectRatio
        };
        a.Warnings = Suitability(type, kind, f.Width, f.Height, f.Transparency);
        return a;
    }

    private async Task<FetchedImage> FetchCachedAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGet(url, out var hit))
        {
            return hit!;
        }

        var img = await _fetcher.FetchAsync(url, ct).ConfigureAwait(false);
        _cache.Add(url, img);
        return img;
    }
}
