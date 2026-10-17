using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AdminToolkit.Core;

public enum DuplicateSignal
{
    /// <summary>Two or more items carry the same TMDb/TVDb/IMDb id ("certain").</summary>
    ProviderId,

    /// <summary>Same normalised title and year, no conflicting provider ids ("likely").</summary>
    TitleYear
}

public enum Confidence
{
    High,
    Medium,
    Low
}

/// <summary>A candidate duplicate group found by <see cref="DuplicateDetector"/> (before enrichment and scoring).</summary>
public sealed class CandidateGroup
{
    public MediaKind Kind { get; set; }

    public List<Guid> ItemIds { get; set; } = new();

    public HashSet<DuplicateSignal> Signals { get; set; } = new();

    /// <summary>Gets or sets the human-readable evidence ("Same TMDb id 603", "Same title and year: matrix (1999)").</summary>
    public List<string> Evidence { get; set; } = new();
}

/// <summary>Finds duplicate candidates among movies and series using provider ids first, then normalised title + year.</summary>
public static class DuplicateDetector
{
    private static readonly string[] Providers = { "Tmdb", "Tvdb", "Imdb" };

    public static List<CandidateGroup> Find(IReadOnlyList<ItemSnapshot> items)
    {
        var groups = new List<CandidateGroup>();
        foreach (var kindGroup in items.Where(i => i.Kind is MediaKind.Movie or MediaKind.Series && !i.IsVirtual).GroupBy(i => i.Kind))
        {
            groups.AddRange(FindInKind(kindGroup.Key, kindGroup.ToList()));
        }

        return groups;
    }

    /// <summary>Duplicate episodes inside one series: the same season/episode number on two or more separate items.</summary>
    public static List<CandidateGroup> FindEpisodeDuplicates(IReadOnlyList<ItemSnapshot> episodesOfOneSeries)
    {
        var result = new List<CandidateGroup>();
        var byKey = new Dictionary<(int Season, int Episode), List<ItemSnapshot>>();
        foreach (var e in episodesOfOneSeries.Where(e => !e.IsVirtual && e.SeasonNumber is not null && e.EpisodeNumber is not null))
        {
            var key = (e.SeasonNumber!.Value, e.EpisodeNumber!.Value);
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = new List<ItemSnapshot>();
            }

            list.Add(e);
        }

        foreach (var (key, list) in byKey.OrderBy(k => k.Key.Season).ThenBy(k => k.Key.Episode))
        {
            if (list.Select(e => e.Id).Distinct().Count() < 2)
            {
                continue;
            }

            result.Add(new CandidateGroup
            {
                Kind = MediaKind.Episode,
                ItemIds = list.Select(e => e.Id).Distinct().ToList(),
                Signals = { DuplicateSignal.TitleYear },
                Evidence = { string.Create(CultureInfo.InvariantCulture, $"Same episode number S{key.Season:00}E{key.Episode:00} in one series, not merged by Jellyfin") }
            });
        }

        return result;
    }

    /// <summary>True when two items carry the same provider name with different values (e.g. two different TMDb ids).</summary>
    public static bool HasProviderConflict(ItemSnapshot a, ItemSnapshot b, out string? detail)
    {
        foreach (var p in Providers)
        {
            var va = a.ProviderId(p);
            var vb = b.ProviderId(p);
            if (va is not null && vb is not null && !string.Equals(va, vb, StringComparison.OrdinalIgnoreCase))
            {
                detail = $"{p} ids differ ({va} vs {vb})";
                return true;
            }
        }

        detail = null;
        return false;
    }

    /// <summary>Stable id of a group: hash of its sorted item ids, so cached choices survive a rescan.</summary>
    public static string GroupId(IEnumerable<Guid> ids)
    {
        var joined = string.Join(",", ids.Select(i => i.ToString("N", CultureInfo.InvariantCulture)).OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    private static IEnumerable<CandidateGroup> FindInKind(MediaKind kind, List<ItemSnapshot> items)
    {
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < items.Count; i++)
        {
            index[items[i].Id] = i;
        }

        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        var evidence = new Dictionary<(int A, int B), (DuplicateSignal Signal, string Text)>();
        void Link(int a, int b, DuplicateSignal signal, string text)
        {
            Union(a, b);
            var key = a < b ? (a, b) : (b, a);
            if (!evidence.TryGetValue(key, out var existing) || signal < existing.Signal)
            {
                evidence[key] = (signal, text);
            }
        }

        // 1. Provider ids (strongest). Includes ids written in folder names ("[tmdbid-603]").
        var byProvider = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            var ids = new Dictionary<string, string>(items[i].ProviderIds, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in TitleNormalizer.ProviderIdsFromName(System.IO.Path.GetFileName(PathText.Normalize(items[i].FolderPath ?? items[i].Path))))
            {
                ids.TryAdd(k, v);
            }

            foreach (var p in Providers)
            {
                if (ids.TryGetValue(p, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    var key = p + ":" + v.Trim().ToLowerInvariant();
                    if (!byProvider.TryGetValue(key, out var list))
                    {
                        byProvider[key] = list = new List<int>();
                    }

                    list.Add(i);
                }
            }
        }

        foreach (var (key, list) in byProvider)
        {
            for (int j = 1; j < list.Count; j++)
            {
                if (list[j] != list[0])
                {
                    var parts = key.Split(':', 2);
                    Link(list[0], list[j], DuplicateSignal.ProviderId, $"Same {parts[0]} id {parts[1]}");
                }
            }
        }

        // 2. Normalised title + year, unless the items have conflicting provider ids.
        var byTitle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var n = TitleNormalizer.Normalize(items[i].Name);
            if (n.Key.Length == 0)
            {
                continue;
            }

            var year = items[i].Year ?? n.Year;
            var key = n.Key + "|" + (year?.ToString(CultureInfo.InvariantCulture) ?? "?");
            if (!byTitle.TryGetValue(key, out var list))
            {
                byTitle[key] = list = new List<int>();
            }

            list.Add(i);
        }

        foreach (var (key, list) in byTitle)
        {
            if (list.Count < 2 || key.EndsWith("|?", StringComparison.Ordinal))
            {
                // Title-only matches without any year are too weak to report.
                continue;
            }

            for (int a = 0; a < list.Count; a++)
            {
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (!HasProviderConflict(items[list[a]], items[list[b]], out _))
                    {
                        var year = key[(key.LastIndexOf('|') + 1)..];
                        Link(list[a], list[b], DuplicateSignal.TitleYear, $"Same normalised title and year (\"{items[list[a]].Name}\", {year})");
                    }
                }
            }
        }

        var byRoot = new Dictionary<int, CandidateGroup>();
        foreach (var ((a, b), (signal, text)) in evidence)
        {
            int root = Find(a);
            if (!byRoot.TryGetValue(root, out var g))
            {
                byRoot[root] = g = new CandidateGroup { Kind = kind };
            }

            g.Signals.Add(signal);
            if (!g.Evidence.Contains(text))
            {
                g.Evidence.Add(text);
            }

            foreach (var idx in new[] { a, b })
            {
                if (!g.ItemIds.Contains(items[idx].Id))
                {
                    g.ItemIds.Add(items[idx].Id);
                }
            }
        }

        return byRoot.Values.Where(g => g.ItemIds.Count > 1);
    }
}

/// <summary>Episode comparison of one copy of a series against the other copies.</summary>
public sealed class EpisodeCoverage
{
    public Guid ItemId { get; set; }

    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the number of episodes this copy shares with every other copy.</summary>
    public int SharedWithAll { get; set; }

    /// <summary>Gets or sets the number of episodes only this copy has.</summary>
    public int OnlyHere { get; set; }

    /// <summary>Gets or sets the episodes present in another copy but missing here.</summary>
    public int MissingHere { get; set; }

    public List<string> OnlyHereSample { get; set; } = new();
}

/// <summary>Computes episode overlap between copies of a series (season/episode numbers, specials excluded).</summary>
public static class EpisodeOverlap
{
    public static string Key(int season, int episode) => string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}");

    /// <summary>Expands an episode (including multi-episode files "S01E01-E02") into its keys. Season 0 (specials) excluded.</summary>
    public static IEnumerable<string> Keys(ItemSnapshot e)
    {
        if (e.IsVirtual || e.SeasonNumber is null || e.EpisodeNumber is null || e.SeasonNumber == 0)
        {
            yield break;
        }

        int end = Math.Max(e.EpisodeNumber.Value, Math.Min(e.EpisodeNumberEnd ?? e.EpisodeNumber.Value, e.EpisodeNumber.Value + 50));
        for (int n = e.EpisodeNumber.Value; n <= end; n++)
        {
            yield return Key(e.SeasonNumber.Value, n);
        }
    }

    public static List<EpisodeCoverage> Compute(IReadOnlyDictionary<Guid, HashSet<string>> episodesByCopy)
    {
        var union = new HashSet<string>(episodesByCopy.Values.SelectMany(v => v), StringComparer.Ordinal);
        var result = new List<EpisodeCoverage>();
        foreach (var (id, set) in episodesByCopy)
        {
            var others = episodesByCopy.Where(kv => kv.Key != id).Select(kv => kv.Value).ToList();
            var onlyHere = set.Where(k => others.All(o => !o.Contains(k))).OrderBy(k => k, StringComparer.Ordinal).ToList();
            result.Add(new EpisodeCoverage
            {
                ItemId = id,
                EpisodeCount = set.Count,
                SharedWithAll = set.Count(k => others.All(o => o.Contains(k))),
                OnlyHere = onlyHere.Count,
                MissingHere = union.Count - set.Count,
                OnlyHereSample = onlyHere.Take(12).ToList()
            });
        }

        return result;
    }
}
