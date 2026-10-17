using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Adapters;

/// <summary>What to load for a list of items.</summary>
public sealed class ItemQuery
{
    public IReadOnlyList<MediaKind> Kinds { get; set; } = new[] { MediaKind.Movie, MediaKind.Series };

    public Guid? LibraryId { get; set; }

    public Guid? ParentId { get; set; }

    public string? NameContains { get; set; }

    public bool IncludeVirtual { get; set; }

    public int? Limit { get; set; }

    /// <summary>Gets or sets a value indicating whether media streams (resolution, codecs, languages) are read; slower.</summary>
    public bool WithStreams { get; set; } = true;
}

/// <summary>Library access. Implemented over ILibraryManager; every Jellyfin call is wrapped by <see cref="JellyfinCall"/>.</summary>
public interface ILibraryAdapter
{
    IReadOnlyList<LibraryInfo> GetLibraries();

    IReadOnlyList<ItemSnapshot> GetItems(ItemQuery query);

    ItemSnapshot? GetItem(Guid id, bool withStreams = true);

    /// <summary>Episodes of a series (optionally including Jellyfin's virtual "missing" episodes).</summary>
    IReadOnlyList<ItemSnapshot> GetEpisodes(Guid seriesId, bool includeMissing);

    /// <summary>Seasons of a series.</summary>
    IReadOnlyList<ItemSnapshot> GetSeasons(Guid seriesId);

    /// <summary>Removes the item from the Jellyfin library; with <paramref name="deleteFiles"/> Jellyfin also deletes its files.</summary>
    void RemoveItem(Guid id, bool deleteFiles);

    /// <summary>Queues a metadata refresh (the same call as "Refresh metadata" in the dashboard). For a library folder this rescans it.</summary>
    void QueueRefresh(Guid id, bool replaceAllMetadata);
}

/// <summary>Users and per-user play data.</summary>
public interface IUserAdapter
{
    IReadOnlyList<UserRef> GetUsers();

    PlayState? GetPlayState(Guid userId, Guid itemId);

    void SavePlayState(Guid userId, Guid itemId, PlayState state);
}

/// <summary>Item images.</summary>
public interface IImageAdapter
{
    IReadOnlyList<ImageSlot> GetImages(Guid itemId);

    IReadOnlyList<string> GetRemoteProviders(Guid itemId);

    Task<IReadOnlyList<RemoteImageDto>> GetRemoteImagesAsync(Guid itemId, string? imageType, bool allLanguages, CancellationToken ct);

    /// <summary>Saves an image the way Jellyfin's own image upload does (IProviderManager.SaveImage, then UpdateToRepository).</summary>
    Task SaveImageAsync(Guid itemId, Stream data, string mimeType, string imageType, int? index, CancellationToken ct);

    Task DeleteImageAsync(Guid itemId, string imageType, int index, CancellationToken ct);

    Task SwapImagesAsync(Guid itemId, string imageType, int index1, int index2, CancellationToken ct);

    Task SetLockedAsync(Guid itemId, bool locked, CancellationToken ct);

    bool IsLocked(Guid itemId);
}

/// <summary>Runs every adapter once and reports which Jellyfin APIs work on this server.</summary>
public sealed class SelfCheckResult
{
    public string Name { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string Detail { get; set; } = string.Empty;
}
