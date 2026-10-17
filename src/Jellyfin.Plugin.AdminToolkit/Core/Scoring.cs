using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AdminToolkit.Configuration;

namespace Jellyfin.Plugin.AdminToolkit.Core;

/// <summary>Who has watched a copy.</summary>
public sealed class CopyWatch
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the played items (1 for a movie, number of played episodes for a series).</summary>
    public int PlayedCount { get; set; }

    public bool Favorite { get; set; }

    public bool HasResume { get; set; }

    public DateTime? LastPlayed { get; set; }
}

/// <summary>One copy in a duplicate group, enriched with disk, drive, Sonarr/Radarr and watch data.</summary>
public sealed class DuplicateCopy
{
    public ItemSnapshot Item { get; set; } = new();

    public string? Drive { get; set; }

    public long? DriveFreeBytes { get; set; }

    public long? DriveTotalBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the item's path exists on disk right now.</summary>
    public bool PathExists { get; set; }

    public int FileCount { get; set; }

    public int FilesMissing { get; set; }

    public long TotalSizeBytes { get; set; }

    /// <summary>Gets or sets whether Sonarr/Radarr manages this path (null when the app is not configured or unreachable).</summary>
    public bool? ArrManaged { get; set; }

    public string? ArrService { get; set; }

    /// <summary>Gets or sets the path Sonarr/Radarr has for this title (translated to Jellyfin's view).</summary>
    public string? ArrPath { get; set; }

    public int? ArrId { get; set; }

    public string? ArrProblem { get; set; }

    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the expected number of (non-special) episodes, from Jellyfin's missing-episode data or the union of copies.</summary>
    public int? ExpectedEpisodes { get; set; }

    public string? ExpectedSource { get; set; }

    public EpisodeCoverage? Coverage { get; set; }

    public List<CopyWatch> Watches { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether another copy uses the same file(s) (same file in two libraries).</summary>
    public bool SharesFilesWithAnotherCopy { get; set; }

    public int Score { get; set; }

    public List<ScoreFactor> Factors { get; set; } = new();

    /// <summary>Gets or sets "Keep" or "Remove" for the current choice.</summary>
    public string Decision { get; set; } = string.Empty;

    public bool Recommended { get; set; }

    public List<string> Reasons { get; set; } = new();

    public List<string> Warnings { get; set; } = new();
}

/// <summary>One scoring signal for one copy.</summary>
public sealed class ScoreFactor
{
    public string Name { get; set; } = string.Empty;

    public int Points { get; set; }

    public int Max { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>A scored duplicate group as shown in the Duplicates tab.</summary>
public sealed class DuplicateGroup
{
    public string Id { get; set; } = string.Empty;

    public MediaKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public Guid PosterItemId { get; set; }

    public Guid? SeriesId { get; set; }

    public List<DuplicateSignal> Signals { get; set; } = new();

    public List<string> Evidence { get; set; } = new();

    public List<DuplicateCopy> Copies { get; set; } = new();

    public Guid RecommendedKeepId { get; set; }

    public Guid KeepId { get; set; }

    public bool Overridden => KeepId != RecommendedKeepId;

    public Confidence Confidence { get; set; }

    public string ConfidenceReason { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the removal list pre-selects this group (never for Low confidence).</summary>
    public bool AutoSelect { get; set; }

    public List<string> Notes { get; set; } = new();
}

/// <summary>What the scorer knows about the environment.</summary>
public sealed class ScoringContext
{
    public ScoreWeights Weights { get; set; } = new();

    public string? PreferredDrive { get; set; }

    /// <summary>Gets or sets a value indicating whether Sonarr (series) / Radarr (movies) is configured and answered.</summary>
    public bool ArrAvailable { get; set; }

    public string ArrName { get; set; } = "Sonarr/Radarr";
}

/// <summary>
/// Transparent keep/remove scoring. Each signal gives a copy between 0 and its weight in points and adds a plain-English
/// reason. The copy with the highest total is recommended to KEEP. Free space on a drive is deliberately not a factor.
/// </summary>
public static class DuplicateScorer
{
    public const string FreeSpaceNote = "Free space on the drive is NOT a factor.";

    public static void Score(DuplicateGroup group, ScoringContext ctx)
    {
        var w = ctx.Weights;
        var copies = group.Copies;
        if (copies.Count == 0)
        {
            return;
        }

        foreach (var c in copies)
        {
            c.Factors.Clear();
        }

        ScoreManaged(group, ctx);
        if (group.Kind == MediaKind.Series)
        {
            ScoreCompleteness(copies, w.Completeness);
        }

        ScoreOnDisk(copies, w.OnDisk);
        ScoreQuality(copies, w.Quality);
        ScoreMetadata(copies, w.Metadata);
        ScoreWatch(copies, w.WatchHistory, group.Kind);
        ScoreDate(copies, w.DateAdded, w.PreferOlder);
        if (!string.IsNullOrWhiteSpace(ctx.PreferredDrive) && w.PreferredDrive > 0)
        {
            foreach (var c in copies)
            {
                bool on = string.Equals(c.Drive, ctx.PreferredDrive, StringComparison.OrdinalIgnoreCase);
                Add(c, "Preferred drive", on ? w.PreferredDrive : 0, w.PreferredDrive, on ? $"on the preferred drive \"{ctx.PreferredDrive}\"" : $"not on the preferred drive \"{ctx.PreferredDrive}\"");
            }
        }

        foreach (var c in copies)
        {
            c.Score = c.Factors.Sum(f => f.Points);
        }

        var ranked = copies
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.ArrManaged == true)
            .ThenByDescending(c => c.EpisodeCount)
            .ThenByDescending(c => c.PathExists)
            .ThenBy(c => c.Item.DateCreated)
            .ThenBy(c => c.Item.Id)
            .ToList();
        group.RecommendedKeepId = ranked[0].Item.Id;
        if (group.KeepId == Guid.Empty || copies.All(c => c.Item.Id != group.KeepId))
        {
            group.KeepId = group.RecommendedKeepId;
        }

        int gap = ranked.Count > 1 ? ranked[0].Score - ranked[1].Score : int.MaxValue;
        (group.Confidence, group.ConfidenceReason) = Rate(group, ranked, gap, w);
        group.AutoSelect = group.Confidence != Confidence.Low;
        if (!group.Notes.Contains(FreeSpaceNote))
        {
            group.Notes.Add(FreeSpaceNote);
        }

        Evaluate(group, group.KeepId, ctx);
    }

    /// <summary>Applies a keep choice (recommended or the admin's override): sets decisions, reasons and warnings.</summary>
    public static void Evaluate(DuplicateGroup group, Guid keepId, ScoringContext ctx)
    {
        if (group.Copies.All(c => c.Item.Id != keepId))
        {
            throw new ToolkitException("bad_keep", "The chosen copy is not part of this duplicate group.");
        }

        group.KeepId = keepId;
        var keeper = group.Copies.First(c => c.Item.Id == keepId);
        var recommended = group.Copies.First(c => c.Item.Id == group.RecommendedKeepId);
        foreach (var c in group.Copies)
        {
            bool keep = c.Item.Id == keepId;
            c.Recommended = c.Item.Id == group.RecommendedKeepId;
            c.Decision = keep ? "Keep" : "Remove";
            c.Reasons = c.Factors.OrderByDescending(f => f.Max).Select(f => f.Reason).Where(r => r.Length > 0).ToList();
            c.Warnings = new List<string>();
            if (keep)
            {
                if (group.Overridden)
                {
                    c.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"You chose this copy instead of the recommendation (score {c.Score} vs {recommended.Score})."));
                }

                if (!c.PathExists)
                {
                    c.Warnings.Add("This copy's path does not exist on disk right now.");
                }

                continue;
            }

            if (c.ArrManaged == true)
            {
                c.Warnings.Add($"This is the copy {c.ArrService ?? ctx.ArrName} manages ({c.ArrPath}). Removing it through {c.ArrService ?? ctx.ArrName} deletes its entry and files, and {c.ArrService ?? ctx.ArrName} will no longer track this title.");
            }

            if (c.SharesFilesWithAnotherCopy)
            {
                c.Warnings.Add("This copy uses the same files as another copy (same folder in two libraries). Only \"remove from library\" is offered; deleting files would delete the kept copy too.");
            }

            if (c.Coverage is not null && c.Coverage.OnlyHere > 0)
            {
                c.Warnings.Add(string.Create(CultureInfo.InvariantCulture, $"This copy has {c.Coverage.OnlyHere} episode(s) the kept copy does not have ({string.Join(", ", c.Coverage.OnlyHereSample)}{(c.Coverage.OnlyHere > c.Coverage.OnlyHereSample.Count ? ", ..." : string.Empty)}).") );
            }

            var unmatchedWatchers = c.Watches.Where(wt => keeper.Watches.All(k => k.UserId != wt.UserId)).Select(wt => wt.UserName).ToList();
            if (unmatchedWatchers.Count > 0)
            {
                c.Warnings.Add($"Watch data on this copy ({string.Join(", ", unmatchedWatchers)}) is not on the kept copy; you can copy it before removing.");
            }
        }
    }

    public static int ResolutionClass(int? width, int? height)
    {
        int h = height ?? 0;
        int wd = width ?? 0;
        if (h >= 1600 || wd >= 3200)
        {
            return 4;
        }

        if (h >= 900 || wd >= 1700)
        {
            return 3;
        }

        if (h >= 650 || wd >= 1200)
        {
            return 2;
        }

        return h > 0 || wd > 0 ? 1 : 0;
    }

    public static string ResolutionName(int cls) => cls switch
    {
        4 => "2160p",
        3 => "1080p",
        2 => "720p",
        1 => "SD",
        _ => "unknown resolution"
    };

    private static (Confidence Level, string Reason) Rate(DuplicateGroup group, List<DuplicateCopy> ranked, int gap, ScoreWeights w)
    {
        var best = ranked[0];
        if (group.Copies.Count > 1)
        {
            for (int a = 0; a < group.Copies.Count; a++)
            {
                for (int b = a + 1; b < group.Copies.Count; b++)
                {
                    if (DuplicateDetector.HasProviderConflict(group.Copies[a].Item, group.Copies[b].Item, out var detail))
                    {
                        return (Confidence.Low, $"Low: the copies have conflicting provider ids ({detail}); they may be different titles. Not auto-selected.");
                    }
                }
            }
        }

        if (group.Copies.Any(c => c.SharesFilesWithAnotherCopy))
        {
            return (Confidence.Low, "Low: two copies point at the same files (one folder in two libraries). Check the library folders; not auto-selected.");
        }

        if (!best.PathExists)
        {
            return (Confidence.Low, "Low: the best-scoring copy is not on disk right now (drive not mounted?). Not auto-selected.");
        }

        if (gap < w.MediumConfidenceGap)
        {
            return (Confidence.Low, string.Create(CultureInfo.InvariantCulture, $"Low: the top two copies score within {gap} point(s) of each other (Medium needs {w.MediumConfidenceGap}). Not auto-selected; choose yourself."));
        }

        if (!group.Signals.Contains(DuplicateSignal.ProviderId))
        {
            return (Confidence.Medium, "Medium: matched by title and year only (no shared provider id).");
        }

        if (gap >= w.HighConfidenceGap)
        {
            return (Confidence.High, string.Create(CultureInfo.InvariantCulture, $"High: same provider id and the recommended copy leads by {gap} points."));
        }

        return (Confidence.Medium, string.Create(CultureInfo.InvariantCulture, $"Medium: the recommended copy leads by {gap} points (High needs {w.HighConfidenceGap})."));
    }

    private static void Add(DuplicateCopy c, string name, int points, int max, string reason)
        => c.Factors.Add(new ScoreFactor { Name = name, Points = Math.Clamp(points, 0, Math.Max(0, max)), Max = max, Reason = reason });

    private static void ScoreManaged(DuplicateGroup group, ScoringContext ctx)
    {
        int max = ctx.Weights.ManagedPath;
        if (!ctx.ArrAvailable)
        {
            var note = $"{ctx.ArrName} is not configured or did not answer, so the managed-path signal was not used.";
            if (!group.Notes.Contains(note))
            {
                group.Notes.Add(note);
            }

            return;
        }

        bool anyManaged = group.Copies.Any(c => c.ArrManaged == true);
        foreach (var c in group.Copies)
        {
            var svc = c.ArrService ?? ctx.ArrName;
            if (c.ArrManaged == true)
            {
                Add(c, "Managed path", max, max, $"path matches {svc}'s path ({c.ArrPath})");
            }
            else if (anyManaged)
            {
                Add(c, "Managed path", 0, max, $"not the path {svc} manages");
            }
            else
            {
                Add(c, "Managed path", 0, max, c.ArrProblem ?? $"no copy is managed by {svc}");
            }
        }
    }

    private static void ScoreCompleteness(List<DuplicateCopy> copies, int max)
    {
        int expected = copies.Max(c => c.ExpectedEpisodes ?? 0);
        int most = copies.Max(c => c.EpisodeCount);
        int denom = Math.Max(expected, most);
        foreach (var c in copies)
        {
            int pts = denom == 0 ? 0 : (int)Math.Round(max * (double)c.EpisodeCount / denom);
            var others = copies.Where(o => o != c).Select(o => o.EpisodeCount.ToString(CultureInfo.InvariantCulture));
            string reason = string.Create(CultureInfo.InvariantCulture, $"has {c.EpisodeCount}/{denom} episodes (other cop{(copies.Count > 2 ? "ies" : "y")}: {string.Join(", ", others)}/{denom})");
            if (c.Coverage is not null && c.Coverage.OnlyHere > 0)
            {
                reason += string.Create(CultureInfo.InvariantCulture, $"; {c.Coverage.OnlyHere} only in this copy");
            }

            Add(c, "Completeness", pts, max, reason);
        }
    }

    private static void ScoreOnDisk(List<DuplicateCopy> copies, int max)
    {
        foreach (var c in copies)
        {
            if (!c.PathExists)
            {
                Add(c, "On disk", 0, max, $"path not found on disk right now ({c.Item.Path}); is the drive mounted?");
                continue;
            }

            if (c.FileCount == 0)
            {
                Add(c, "On disk", max / 2, max, "folder exists but has no media files Jellyfin knows about");
                continue;
            }

            int present = c.FileCount - c.FilesMissing;
            int pts = (int)Math.Round(max * (double)present / c.FileCount);
            Add(c, "On disk", pts, max, c.FilesMissing == 0
                ? string.Create(CultureInfo.InvariantCulture, $"all {c.FileCount} file(s) exist on disk")
                : string.Create(CultureInfo.InvariantCulture, $"{c.FilesMissing} of {c.FileCount} file(s) missing on disk"));
        }
    }

    private static void ScoreQuality(List<DuplicateCopy> copies, int max)
    {
        var classes = copies.ToDictionary(c => c, c => ResolutionClass(c.Item.Width, c.Item.Height));
        int best = classes.Values.Max();
        bool allSame = classes.Values.Distinct().Count() == 1;
        foreach (var c in copies)
        {
            int cls = classes[c];
            string desc = Describe(c.Item, cls);
            if (best == 0)
            {
                Add(c, "Quality", 0, max, "video quality unknown (not probed yet)");
            }
            else if (allSame)
            {
                Add(c, "Quality", max, max, $"same resolution as the other copies ({desc}); file size is not used");
            }
            else
            {
                int pts = (int)Math.Round(max * (double)cls / best);
                Add(c, "Quality", pts, max, cls == best ? $"highest resolution ({desc})" : $"lower resolution ({desc} vs {ResolutionName(best)})");
            }
        }
    }

    private static string Describe(ItemSnapshot i, int cls)
    {
        var parts = new List<string> { ResolutionName(cls) };
        if (!string.IsNullOrEmpty(i.VideoCodec))
        {
            parts.Add(i.VideoCodec.ToUpperInvariant());
        }

        if (!string.IsNullOrEmpty(i.VideoRange) && !i.VideoRange.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(i.VideoRange);
        }

        return string.Join(" ", parts);
    }

    private static void ScoreMetadata(List<DuplicateCopy> copies, int max)
    {
        foreach (var c in copies)
        {
            var i = c.Item;
            double f = 0;
            var have = new List<string>();
            var missing = new List<string>();
            void Part(bool ok, double weight, string name)
            {
                if (ok)
                {
                    f += weight;
                    have.Add(name);
                }
                else
                {
                    missing.Add(name);
                }
            }

            Part(i.HasProviderIds, 0.4, "provider ids");
            Part(i.Images("Primary") > 0, 0.3, "poster");
            Part(i.Images("Backdrop") > 0, 0.15, "backdrop");
            Part(i.HasOverview, 0.15, "overview");
            int pts = (int)Math.Round(max * f);
            var reason = missing.Count == 0 ? "metadata complete (" + string.Join(", ", have) + ")" : "metadata missing: " + string.Join(", ", missing);
            if (i.HasProviderIds)
            {
                reason += " [" + string.Join(", ", i.ProviderIds.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "]";
            }

            Add(c, "Metadata", pts, max, reason);
        }
    }

    private static void ScoreWatch(List<DuplicateCopy> copies, int max, MediaKind kind)
    {
        int most = copies.Max(c => c.Watches.Sum(x => x.PlayedCount));
        bool anyData = copies.Any(c => c.Watches.Count > 0);
        foreach (var c in copies)
        {
            if (!anyData)
            {
                Add(c, "Watch history", 0, max, "nobody has watched any copy");
                continue;
            }

            int played = c.Watches.Sum(x => x.PlayedCount);
            if (c.Watches.Count == 0)
            {
                Add(c, "Watch history", 0, max, "no watch data on this copy");
                continue;
            }

            int pts = most == 0 ? max / 2 : (int)Math.Round(max * (double)played / most);
            var who = string.Join(", ", c.Watches.Select(x => x.UserName));
            Add(c, "Watch history", pts, max, kind == MediaKind.Series
                ? string.Create(CultureInfo.InvariantCulture, $"watched by {who} ({played} episode plays)")
                : $"watched by {who}");
        }
    }

    private static void ScoreDate(List<DuplicateCopy> copies, int max, bool preferOlder)
    {
        if (max <= 0 || copies.Count < 2)
        {
            return;
        }

        var target = preferOlder ? copies.Min(c => c.Item.DateCreated) : copies.Max(c => c.Item.DateCreated);
        foreach (var c in copies)
        {
            bool win = c.Item.DateCreated == target;
            var date = c.Item.DateCreated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Add(c, "Date added", win ? max : 0, max, win
                ? (preferOlder ? $"added first ({date})" : $"added most recently ({date})")
                : $"added {date}");
        }
    }
}
