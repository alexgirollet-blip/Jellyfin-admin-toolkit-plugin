using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AdminToolkit.Core;

public enum MediaKind
{
    Movie,
    Series,
    Season,
    Episode,
    BoxSet,
    Person,
    Other
}

/// <summary>A Jellyfin library (virtual folder).</summary>
public sealed class LibraryInfo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? CollectionType { get; set; }

    public List<string> Locations { get; set; } = new();
}

/// <summary>Plain snapshot of a Jellyfin item, built by the adapter so the core never touches Jellyfin types.</summary>
public sealed class ItemSnapshot
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    /// <summary>Gets or sets the item's path (file for movies/episodes, folder for series/seasons).</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the folder that contains the item (movie folder, series folder).</summary>
    public string? FolderPath { get; set; }

    public Guid? LibraryId { get; set; }

    public string? LibraryName { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime DateCreated { get; set; }

    public bool HasOverview { get; set; }

    public bool IsLocked { get; set; }

    public bool IsVirtual { get; set; }

    /// <summary>Gets or sets the number of images per type name (Primary, Backdrop, Logo...).</summary>
    public Dictionary<string, int> ImageCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the files that make up the item (for movies: all parts and merged versions).</summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>Gets or sets what Jellyfin deletes when "Delete media" is used (often the whole movie or series folder).</summary>
    public List<string> DeletePaths { get; set; } = new();

    public long? SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? VideoCodec { get; set; }

    public int? BitDepth { get; set; }

    /// <summary>Gets or sets the video range type reported by Jellyfin (SDR, HDR10, DOVI, HLG...).</summary>
    public string? VideoRange { get; set; }

    public int? Bitrate { get; set; }

    public string? Container { get; set; }

    public List<string> AudioLanguages { get; set; } = new();

    public List<string> AudioCodecs { get; set; } = new();

    public List<string> SubtitleLanguages { get; set; } = new();

    /// <summary>Gets or sets the number of versions Jellyfin merged into this movie (1 = single version).</summary>
    public int VersionCount { get; set; } = 1;

    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public int? EpisodeNumberEnd { get; set; }

    public bool HasProviderIds => ProviderIds.Count > 0;

    public int Images(string type) => ImageCounts.TryGetValue(type, out var n) ? n : 0;

    public string? ProviderId(string name) => ProviderIds.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>A user's play state for one item (Jellyfin UserItemData without Jellyfin types).</summary>
public sealed class PlayState
{
    public bool Played { get; set; }

    public int PlayCount { get; set; }

    public bool IsFavorite { get; set; }

    public long PlaybackPositionTicks { get; set; }

    public DateTime? LastPlayedDate { get; set; }

    public bool HasAnyData => Played || PlayCount > 0 || IsFavorite || PlaybackPositionTicks > 0 || LastPlayedDate is not null;
}

/// <summary>A Jellyfin user.</summary>
public sealed record UserRef(Guid Id, string Name, bool IsAdministrator, bool IsDisabled);

/// <summary>One image currently set on an item.</summary>
public sealed class ImageSlot
{
    public string Type { get; set; } = string.Empty;

    public int Index { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public string? Path { get; set; }

    public long? FileSize { get; set; }

    public bool IsLocal { get; set; }
}

/// <summary>An image offered by one of Jellyfin's remote image providers (TMDb, Fanart.tv, ...).</summary>
public sealed class RemoteImageDto
{
    public string Url { get; set; } = string.Empty;

    public string? ThumbnailUrl { get; set; }

    public string? Provider { get; set; }

    public string Type { get; set; } = string.Empty;

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Language { get; set; }

    public double? CommunityRating { get; set; }

    public int? VoteCount { get; set; }
}
