using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

/// <summary>In-memory library. RemoveItem(deleteFiles: true) deletes the item's DeletePaths like Jellyfin does.</summary>
public sealed class FakeLibrary : ILibraryAdapter
{
    public List<LibraryInfo> Libraries { get; } = new();

    public List<ItemSnapshot> Items { get; } = new();

    public List<(Guid Id, bool DeleteFiles)> Removed { get; } = new();

    public List<(Guid Id, bool Replace)> Refreshed { get; } = new();

    public IReadOnlyList<LibraryInfo> GetLibraries() => Libraries;

    public IReadOnlyList<ItemSnapshot> GetItems(ItemQuery query)
        => Items.Where(i => query.Kinds.Contains(i.Kind) && (query.IncludeVirtual || !i.IsVirtual))
            .Where(i => query.LibraryId is null || i.LibraryId == query.LibraryId)
            .Where(i => string.IsNullOrEmpty(query.NameContains) || i.Name.Contains(query.NameContains, StringComparison.OrdinalIgnoreCase))
            .Take(query.Limit ?? int.MaxValue)
            .ToList();

    public ItemSnapshot? GetItem(Guid id, bool withStreams = true) => Items.FirstOrDefault(i => i.Id == id);

    public IReadOnlyList<ItemSnapshot> GetEpisodes(Guid seriesId, bool includeMissing)
        => Items.Where(i => i.Kind == MediaKind.Episode && i.SeriesId == seriesId && (includeMissing || !i.IsVirtual)).ToList();

    public IReadOnlyList<ItemSnapshot> GetSeasons(Guid seriesId)
        => Items.Where(i => i.Kind == MediaKind.Season && i.SeriesId == seriesId).ToList();

    public void RemoveItem(Guid id, bool deleteFiles)
    {
        var item = Items.First(i => i.Id == id);
        Removed.Add((id, deleteFiles));
        if (deleteFiles)
        {
            foreach (var p in item.DeletePaths)
            {
                if (Directory.Exists(p))
                {
                    Directory.Delete(p, true);
                }
                else if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }

        Items.RemoveAll(i => i.Id == id || i.SeriesId == id);
    }

    public void QueueRefresh(Guid id, bool replaceAllMetadata) => Refreshed.Add((id, replaceAllMetadata));

    /// <summary>Adds a series with real episode files under <paramref name="folder"/>.</summary>
    public ItemSnapshot AddSeries(string name, int year, string folder, int episodes, int expected, bool createFiles = true, params (string Key, string Value)[] ids)
    {
        var s = new ItemSnapshot { Id = Guid.NewGuid(), Name = name, Year = year, Kind = MediaKind.Series, Path = folder, FolderPath = folder, DeletePaths = { folder }, DateCreated = new DateTime(2021, 1, 1), HasOverview = true };
        foreach (var (k, v) in ids)
        {
            s.ProviderIds[k] = v;
        }

        s.ImageCounts["Primary"] = 1;
        Items.Add(s);
        for (int e = 1; e <= expected; e++)
        {
            int season = ((e - 1) / 12) + 1;
            int num = ((e - 1) % 12) + 1;
            var ep = new ItemSnapshot { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "ep" + e, SeriesId = s.Id, SeasonNumber = season, EpisodeNumber = num, Width = 1920, Height = 1080, VideoCodec = "h264", AudioLanguages = { "eng" } };
            if (e <= episodes)
            {
                var file = Path.Combine(folder, $"Season {season}", $"{name} S{season:00}E{num:00}.mkv");
                ep.Path = file;
                ep.FolderPath = Path.GetDirectoryName(file);
                ep.FilePaths.Add(file);
                ep.DeletePaths.Add(file);
                if (createFiles)
                {
                    Directory.CreateDirectory(ep.FolderPath!);
                    File.WriteAllBytes(file, new byte[1000]);
                }
            }
            else
            {
                ep.IsVirtual = true;
            }

            Items.Add(ep);
        }

        return s;
    }
}

public sealed class FakeUsers : IUserAdapter
{
    public List<UserRef> Users { get; } = new();

    public Dictionary<(Guid User, Guid Item), PlayState> Data { get; } = new();

    public IReadOnlyList<UserRef> GetUsers() => Users;

    public PlayState? GetPlayState(Guid userId, Guid itemId) => Data.TryGetValue((userId, itemId), out var s) ? s : new PlayState();

    public void SavePlayState(Guid userId, Guid itemId, PlayState state) => Data[(userId, itemId)] = state;
}

public sealed class FakeSpace : IDriveSpaceProvider
{
    public DriveSpace? GetSpace(string path) => Directory.Exists(path) ? new DriveSpace(8_000_000_000_000, 2_000_000_000_000) : null;
}

public sealed class FakeImages : IImageAdapter
{
    public List<ImageSlot> Slots { get; } = new();

    public List<string> Calls { get; } = new();

    public bool Locked { get; set; }

    public IReadOnlyList<ImageSlot> GetImages(Guid itemId) => Slots;

    public IReadOnlyList<string> GetRemoteProviders(Guid itemId) => new[] { "TheMovieDb", "Fanart" };

    public Task<IReadOnlyList<RemoteImageDto>> GetRemoteImagesAsync(Guid itemId, string? imageType, bool allLanguages, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RemoteImageDto>>(new[] { new RemoteImageDto { Url = "https://image.tmdb.org/t/p/original/x.png", Provider = "TheMovieDb", Type = imageType ?? "Primary", Width = 1000, Height = 1500, Language = "en" } });

    public Task SaveImageAsync(Guid itemId, Stream data, string mimeType, string imageType, int? index, CancellationToken ct)
    {
        Calls.Add($"save {imageType} {index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} {mimeType} {data.Length}");
        return Task.CompletedTask;
    }

    public Task DeleteImageAsync(Guid itemId, string imageType, int index, CancellationToken ct)
    {
        Calls.Add($"delete {imageType} {index}");
        return Task.CompletedTask;
    }

    public Task SwapImagesAsync(Guid itemId, string imageType, int index1, int index2, CancellationToken ct)
    {
        Calls.Add($"swap {imageType} {index1} {index2}");
        return Task.CompletedTask;
    }

    public Task SetLockedAsync(Guid itemId, bool locked, CancellationToken ct)
    {
        Locked = locked;
        Calls.Add($"lock {locked}");
        return Task.CompletedTask;
    }

    public bool IsLocked(Guid itemId) => Locked;
}
