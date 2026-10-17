using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.AdminToolkit.Adapters;

/// <summary>
/// <see cref="IImageAdapter"/> using the same calls as Jellyfin's own image endpoints in 10.11.10:
/// IProviderManager.SaveImage(BaseItem, Stream, string, ImageType, int?, CancellationToken) followed by
/// BaseItem.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate), BaseItem.DeleteImageAsync, BaseItem.SwapImagesAsync,
/// IProviderManager.GetAvailableRemoteImages(BaseItem, RemoteImageQuery, CancellationToken).
/// </summary>
public sealed class JellyfinImageAdapter : IImageAdapter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    public JellyfinImageAdapter(ILibraryManager libraryManager, IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    public static ImageType ParseType(string type)
        => Enum.TryParse<ImageType>(type, true, out var t) && Enum.IsDefined(t)
            ? t
            : throw new ToolkitException("bad_image_type", $"Unknown image type \"{type}\".");

    public IReadOnlyList<ImageSlot> GetImages(Guid itemId)
    {
        var item = Item(itemId);
        return JellyfinCall.Run("list the item's images", () =>
        {
            var list = new List<ImageSlot>();
            foreach (var img in item.ImageInfos ?? Array.Empty<ItemImageInfo>())
            {
                long? size = null;
                if (img.IsLocalFile && !string.IsNullOrEmpty(img.Path))
                {
                    try
                    {
                        var fi = new FileInfo(img.Path);
                        size = fi.Exists ? fi.Length : null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                list.Add(new ImageSlot
                {
                    Type = img.Type.ToString(),
                    Index = item.GetImageIndex(img),
                    Width = img.Width,
                    Height = img.Height,
                    Path = img.Path,
                    FileSize = size,
                    IsLocal = img.IsLocalFile
                });
            }

            return list.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Index).ToList();
        });
    }

    public IReadOnlyList<string> GetRemoteProviders(Guid itemId)
    {
        var item = Item(itemId);
        return JellyfinCall.Run("list remote image providers", () => _providerManager.GetRemoteImageProviderInfo(item).Select(p => p.Name).ToList());
    }

    public async Task<IReadOnlyList<RemoteImageDto>> GetRemoteImagesAsync(Guid itemId, string? imageType, bool allLanguages, CancellationToken ct)
    {
        var item = Item(itemId);
        ImageType? type = string.IsNullOrWhiteSpace(imageType) ? null : ParseType(imageType);
        var images = await JellyfinCall.RunAsync("get remote images", () => _providerManager.GetAvailableRemoteImages(
            item,
            new RemoteImageQuery(string.Empty) { ImageType = type, IncludeAllLanguages = allLanguages, IncludeDisabledProviders = false },
            ct)).ConfigureAwait(false);
        return JellyfinCall.Run("read remote images", () => images
            .Where(i => !string.IsNullOrEmpty(i.Url))
            .Select(i => new RemoteImageDto
            {
                Url = i.Url,
                ThumbnailUrl = i.ThumbnailUrl,
                Provider = i.ProviderName,
                Type = i.Type.ToString(),
                Width = i.Width,
                Height = i.Height,
                Language = i.Language,
                CommunityRating = i.CommunityRating,
                VoteCount = i.VoteCount
            })
            .ToList());
    }

    public async Task SaveImageAsync(Guid itemId, Stream data, string mimeType, string imageType, int? index, CancellationToken ct)
    {
        var item = Item(itemId);
        var type = ParseType(imageType);
        await JellyfinCall.RunAsync("save the image", () => _providerManager.SaveImage(item, data, mimeType, type, index, ct)).ConfigureAwait(false);
        await JellyfinCall.RunAsync("update the item after saving the image", () => item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct)).ConfigureAwait(false);
    }

    public Task DeleteImageAsync(Guid itemId, string imageType, int index, CancellationToken ct)
    {
        var item = Item(itemId);
        var type = ParseType(imageType);
        return JellyfinCall.RunAsync("delete the image", () => item.DeleteImageAsync(type, index));
    }

    public Task SwapImagesAsync(Guid itemId, string imageType, int index1, int index2, CancellationToken ct)
    {
        var item = Item(itemId);
        var type = ParseType(imageType);
        return JellyfinCall.RunAsync("reorder images", () => item.SwapImagesAsync(type, index1, index2));
    }

    public async Task SetLockedAsync(Guid itemId, bool locked, CancellationToken ct)
    {
        var item = Item(itemId);
        JellyfinCall.Run("change the item lock", () => item.IsLocked = locked);
        await JellyfinCall.RunAsync("save the item lock", () => item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)).ConfigureAwait(false);
    }

    public bool IsLocked(Guid itemId)
    {
        var item = Item(itemId);
        return JellyfinCall.Run("read the item lock", () => item.IsLocked);
    }

    private BaseItem Item(Guid id)
        => JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(id))
           ?? throw new ToolkitException("not_found", "The item no longer exists in Jellyfin.", 404);
}
