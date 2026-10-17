using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AdminToolkit.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.AdminToolkit.Adapters;

/// <summary>
/// <see cref="ILibraryAdapter"/> over Jellyfin 10.11.10's ILibraryManager / IProviderManager.
/// Signatures used (verified against the 10.11.10 NuGet packages): GetVirtualFolders(), GetItemList(InternalItemsQuery),
/// GetItemById(Guid), DeleteItem(BaseItem, DeleteOptions, bool), IProviderManager.QueueRefresh(Guid, MetadataRefreshOptions, RefreshPriority),
/// BaseItem.GetMediaStreams(), BaseItem.GetDeletePaths(), Folder.GetRecursiveChildren(Func).
/// </summary>
public sealed class JellyfinLibraryAdapter : ILibraryAdapter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    public JellyfinLibraryAdapter(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    public IReadOnlyList<LibraryInfo> GetLibraries()
        => JellyfinCall.Run("list the libraries", () => _libraryManager.GetVirtualFolders()
            .Select(v => new LibraryInfo
            {
                Id = Guid.TryParse(v.ItemId, out var id) ? id : Guid.Empty,
                Name = v.Name ?? string.Empty,
                CollectionType = v.CollectionType?.ToString(),
                Locations = (v.Locations ?? Array.Empty<string>()).ToList()
            })
            .ToList());

    public IReadOnlyList<ItemSnapshot> GetItems(ItemQuery query)
    {
        var libs = GetLibraries();
        var kinds = query.Kinds.Select(ToKind).Where(k => k is not null).Select(k => k!.Value).Distinct().ToArray();
        var items = JellyfinCall.Run("list library items", () =>
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = kinds,
                Recursive = true,
                IsVirtualItem = query.IncludeVirtual ? null : false,
                NameContains = string.IsNullOrWhiteSpace(query.NameContains) ? null : query.NameContains.Trim(),
                Limit = query.Limit
            };
            if (query.LibraryId is { } lib && lib != Guid.Empty)
            {
                q.AncestorIds = new[] { lib };
            }

            if (query.ParentId is { } parent && parent != Guid.Empty)
            {
                q.ParentId = parent;
            }

            return _libraryManager.GetItemList(q);
        });

        return items.Select(i => Snapshot(i, libs, query.WithStreams)).ToList();
    }

    public ItemSnapshot? GetItem(Guid id, bool withStreams = true)
    {
        var item = JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(id));
        return item is null ? null : Snapshot(item, GetLibraries(), withStreams);
    }

    public IReadOnlyList<ItemSnapshot> GetEpisodes(Guid seriesId, bool includeMissing)
    {
        var libs = GetLibraries();
        var episodes = JellyfinCall.Run("list the episodes of a series", () =>
        {
            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                return (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
            }

            return series.GetRecursiveChildren(i => i is Episode && (includeMissing || !i.IsVirtualItem));
        });
        return episodes.Select(e => Snapshot(e, libs, true)).ToList();
    }

    public IReadOnlyList<ItemSnapshot> GetSeasons(Guid seriesId)
    {
        var libs = GetLibraries();
        var seasons = JellyfinCall.Run("list the seasons of a series", () =>
        {
            if (_libraryManager.GetItemById(seriesId) is not Series series)
            {
                return (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
            }

            return series.GetRecursiveChildren(i => i is Season);
        });
        return seasons.Select(e => Snapshot(e, libs, false)).ToList();
    }

    public void RemoveItem(Guid id, bool deleteFiles)
    {
        var item = JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(id))
            ?? throw new ToolkitException("not_found", "The item no longer exists in Jellyfin.", 404);
        JellyfinCall.Run(deleteFiles ? "delete " + item.Name + " and its files" : "remove " + item.Name + " from the library", () =>
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = deleteFiles }, true));
    }

    public void QueueRefresh(Guid id, bool replaceAllMetadata)
    {
        JellyfinCall.Run("queue a metadata refresh", () =>
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = replaceAllMetadata ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
                ImageRefreshMode = replaceAllMetadata ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
                ReplaceAllMetadata = replaceAllMetadata,
                ReplaceAllImages = false,
                IsAutomated = false
            };
            _providerManager.QueueRefresh(id, options, RefreshPriority.Normal);
        });
    }

    internal static MediaKind KindOf(BaseItem item) => item switch
    {
        Movie => MediaKind.Movie,
        Series => MediaKind.Series,
        Season => MediaKind.Season,
        Episode => MediaKind.Episode,
        BoxSet => MediaKind.BoxSet,
        Person => MediaKind.Person,
        _ => MediaKind.Other
    };

    internal static BaseItemKind? ToKind(MediaKind kind) => kind switch
    {
        MediaKind.Movie => BaseItemKind.Movie,
        MediaKind.Series => BaseItemKind.Series,
        MediaKind.Season => BaseItemKind.Season,
        MediaKind.Episode => BaseItemKind.Episode,
        MediaKind.BoxSet => BaseItemKind.BoxSet,
        MediaKind.Person => BaseItemKind.Person,
        _ => null
    };

    private static ItemSnapshot Snapshot(BaseItem item, IReadOnlyList<LibraryInfo> libs, bool withStreams)
    {
        var s = JellyfinCall.Run("read item details", () =>
        {
            var snap = new ItemSnapshot
            {
                Id = item.Id,
                Name = item.Name ?? string.Empty,
                Year = item.ProductionYear,
                Kind = KindOf(item),
                Path = item.Path,
                FolderPath = item.IsFolder ? item.Path : item.ContainingFolderPath,
                ProviderIds = new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                DateCreated = item.DateCreated,
                HasOverview = !string.IsNullOrWhiteSpace(item.Overview),
                IsLocked = item.IsLocked,
                IsVirtual = item.IsVirtualItem,
                SizeBytes = item.Size,
                Width = item.Width > 0 ? item.Width : null,
                Height = item.Height > 0 ? item.Height : null,
                Bitrate = item.TotalBitrate,
                Container = item.Container
            };
            foreach (var img in item.ImageInfos ?? Array.Empty<ItemImageInfo>())
            {
                var key = img.Type.ToString();
                snap.ImageCounts[key] = snap.ImageCounts.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            if (item is Video video)
            {
                if (!string.IsNullOrEmpty(video.Path))
                {
                    snap.FilePaths.Add(video.Path);
                }

                snap.FilePaths.AddRange(video.AdditionalParts ?? Array.Empty<string>());
                snap.FilePaths.AddRange(video.LocalAlternateVersions ?? Array.Empty<string>());
                snap.VersionCount = Math.Max(1, video.MediaSourceCount);
            }

            if (item is Episode ep)
            {
                snap.SeriesId = ep.SeriesId;
                snap.SeriesName = ep.SeriesName;
                snap.SeasonNumber = ep.ParentIndexNumber;
                snap.EpisodeNumber = ep.IndexNumber;
                snap.EpisodeNumberEnd = ep.IndexNumberEnd;
            }
            else if (item is Season season)
            {
                snap.SeriesId = season.SeriesId;
                snap.SeriesName = season.SeriesName;
                snap.SeasonNumber = season.IndexNumber;
            }

            if (!item.IsVirtualItem)
            {
                snap.DeletePaths = item.GetDeletePaths().Select(p => p.FullName).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            }

            return snap;
        });

        var lib = libs
            .SelectMany(l => l.Locations.Select(loc => (Lib: l, Loc: loc)))
            .Where(x => PathText.IsUnder(s.FolderPath ?? s.Path, x.Loc))
            .OrderByDescending(x => x.Loc.Length)
            .FirstOrDefault();
        if (lib.Lib is not null)
        {
            s.LibraryId = lib.Lib.Id;
            s.LibraryName = lib.Lib.Name;
        }

        if (withStreams && item is Video)
        {
            ReadStreams(item, s);
        }

        return s;
    }

    private static void ReadStreams(BaseItem item, ItemSnapshot s)
    {
        var streams = JellyfinCall.Run("read media streams", () => item.GetMediaStreams());
        foreach (var st in streams)
        {
            switch (st.Type)
            {
                case MediaStreamType.Video when s.VideoCodec is null:
                    s.VideoCodec = st.Codec;
                    s.BitDepth = st.BitDepth;
                    s.VideoRange = JellyfinCall.Run("read the video range", () => st.VideoRangeType.ToString());
                    s.Width ??= st.Width;
                    s.Height ??= st.Height;
                    if (st.Width is > 0)
                    {
                        s.Width = st.Width;
                    }

                    if (st.Height is > 0)
                    {
                        s.Height = st.Height;
                    }

                    break;
                case MediaStreamType.Audio:
                    s.AudioLanguages.Add(Lang(st.Language));
                    if (!string.IsNullOrEmpty(st.Codec))
                    {
                        s.AudioCodecs.Add(st.Codec);
                    }

                    break;
                case MediaStreamType.Subtitle:
                    s.SubtitleLanguages.Add(Lang(st.Language));
                    break;
            }
        }

        s.AudioLanguages = s.AudioLanguages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.AudioCodecs = s.AudioCodecs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.SubtitleLanguages = s.SubtitleLanguages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Lang(string? language) => string.IsNullOrWhiteSpace(language) ? "und" : language.Trim().ToLower(CultureInfo.InvariantCulture);
}
