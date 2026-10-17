using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("The Office (US)", "office us")]
    [InlineData("Office, The (US)", "office us")]
    [InlineData("The Matrix (1999)", "matrix")]
    [InlineData("Matrix [1999] 1080p BluRay x264", "matrix")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Law & Order: SVU", "law and order svu")]
    [InlineData("Star Wars: Episode IV - A New Hope", "star wars episode iv a new hope")]
    [InlineData("Breaking Bad [tvdbid-81189]", "breaking bad")]
    public void Normalize_StripsArticlesPunctuationTagsAndYears(string input, string expectedWords)
    {
        Assert.Equal(expectedWords.Replace(" ", string.Empty, StringComparison.Ordinal), TitleNormalizer.Normalize(input).Key);
    }

    [Fact]
    public void Normalize_ExtractsYearFromBrackets()
    {
        Assert.Equal(1999, TitleNormalizer.Normalize("The Matrix (1999)").Year);
        Assert.Equal(2019, TitleNormalizer.Normalize("Joker [2019] 2160p").Year);
        Assert.Null(TitleNormalizer.Normalize("The Matrix").Year);
    }

    [Fact]
    public void Normalize_KeepsNumbersThatArePartOfTheTitle()
    {
        Assert.Equal("bladerunner2049", TitleNormalizer.Normalize("Blade Runner 2049").Key);
        Assert.Equal("2001aspaceodyssey", TitleNormalizer.Normalize("2001: A Space Odyssey").Key);
        Assert.NotEqual(TitleNormalizer.Normalize("Dune").Key, TitleNormalizer.Normalize("Dune: Part Two").Key);
    }

    [Fact]
    public void ProviderIdsFromName_ReadsJellyfinNamingTags()
    {
        var ids = TitleNormalizer.ProviderIdsFromName("The Matrix (1999) [tmdbid-603] {imdb-tt0133093}");
        Assert.Equal("603", ids["Tmdb"]);
        Assert.Equal("tt0133093", ids["Imdb"]);
        Assert.Empty(TitleNormalizer.ProviderIdsFromName("Plain Folder"));
    }
}

public class PathMappingTests
{
    [Fact]
    public void DriveMapper_LongestPrefixWins_AndMatchesWholeSegments()
    {
        var mapper = new DriveMapper(new[]
        {
            new DriveMapping { Prefix = "/data", Label = "1TB" },
            new DriveMapping { Prefix = "/data/8tb1/", Label = "8TB 1" },
            new DriveMapping { Prefix = "/data/8tb2", Label = "8TB 2" }
        });

        Assert.Equal("8TB 1", mapper.LabelFor("/data/8tb1/TV/Show/Season 1/ep.mkv"));
        Assert.Equal("8TB 2", mapper.LabelFor("/data/8tb2"));
        Assert.Equal("1TB", mapper.LabelFor("/data/8tb20/Movies/x.mkv"));
        Assert.Equal("1TB", mapper.LabelFor("/data/other"));
        Assert.Null(mapper.LabelFor("/media/x"));
        Assert.Null(mapper.LabelFor(null));
    }

    [Fact]
    public void DriveMapper_IgnoresEmptyRows_AndNormalisesSlashes()
    {
        var mapper = new DriveMapper(new[] { new DriveMapping { Prefix = "", Label = "x" }, new DriveMapping { Prefix = "/mnt//usb/", Label = "USB" } });
        Assert.Single(mapper.Mappings);
        Assert.Equal("USB", mapper.LabelFor("/mnt/usb/a"));
    }

    [Fact]
    public void ArrPathMapper_TranslatesSonarrPathsToJellyfinPaths()
    {
        var mapper = new ArrPathMapper(new[]
        {
            new PathMapping { ArrPrefix = "/tv", JellyfinPrefix = "/data/8tb2/TV" },
            new PathMapping { ArrPrefix = "/tv/kids", JellyfinPrefix = "/data/1tb/Kids" }
        });
        Assert.Equal("/data/8tb2/TV/Show Name", mapper.ToJellyfin("/tv/Show Name/"));
        Assert.Equal("/data/1tb/Kids/Bluey", mapper.ToJellyfin("/tv/kids/Bluey"));
        Assert.Equal("/tvshows/X", mapper.ToJellyfin("/tvshows/X"));
    }

    [Fact]
    public void PathText_IsUnder_UsesSegments()
    {
        Assert.True(PathText.IsUnder("/a/b/c", "/a/b"));
        Assert.True(PathText.IsUnder("/a/b", "/a/b/"));
        Assert.False(PathText.IsUnder("/a/bc", "/a/b"));
        Assert.Equal("'/x/it'\\''s here'", PathText.ShellQuote("/x/it's here"));
    }

    [Fact]
    public void Secrets_AreMasked_AndMaskKeepsExistingValue()
    {
        Assert.Equal("••••cdef", Secrets.Mask("0123456789abcdef"));
        Assert.Equal(string.Empty, Secrets.Mask(""));
        Assert.Equal("stored", Secrets.Merge("••••ored", "stored"));
        Assert.Equal("new", Secrets.Merge(" new ", "stored"));
        Assert.Equal(string.Empty, Secrets.Merge("", "stored"));
    }
}

internal static class Make
{
    public static ItemSnapshot Item(string name, int? year, MediaKind kind = MediaKind.Movie, string path = "/m", params (string Key, string Value)[] ids)
    {
        var s = new ItemSnapshot { Id = Guid.NewGuid(), Name = name, Year = year, Kind = kind, Path = path, FolderPath = path, DateCreated = new DateTime(2020, 1, 1) };
        foreach (var (k, v) in ids)
        {
            s.ProviderIds[k] = v;
        }

        return s;
    }

    public static ItemSnapshot Episode(int season, int episode, int? end = null)
        => new() { Id = Guid.NewGuid(), Kind = MediaKind.Episode, SeasonNumber = season, EpisodeNumber = episode, EpisodeNumberEnd = end, Name = "e" };
}

public class DuplicateGroupingTests
{
    [Fact]
    public void SameProviderId_GroupsAsCertain()
    {
        var a = Make.Item("The Matrix", 1999, ids: ("Tmdb", "603"));
        var b = Make.Item("Matrix", 1999, ids: ("Tmdb", "603"));
        var c = Make.Item("Inception", 2010, ids: ("Tmdb", "27205"));
        var groups = DuplicateDetector.Find(new[] { a, b, c });

        var g = Assert.Single(groups);
        Assert.Contains(DuplicateSignal.ProviderId, g.Signals);
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), g.ItemIds.OrderBy(x => x));
        Assert.Contains(g.Evidence, e => e.Contains("Tmdb id 603", StringComparison.Ordinal));
    }

    [Fact]
    public void TitleAndYear_GroupsAsLikely_WhenNoConflictingIds()
    {
        var a = Make.Item("The Office (US)", 2005, MediaKind.Series, "/tv1/The Office", ("Tvdb", "73244"));
        var b = Make.Item("Office, The (US)", 2005, MediaKind.Series, "/tv2/Office");
        var g = Assert.Single(DuplicateDetector.Find(new[] { a, b }));
        Assert.Contains(DuplicateSignal.TitleYear, g.Signals);
        Assert.DoesNotContain(DuplicateSignal.ProviderId, g.Signals);
    }

    [Fact]
    public void SameTitleYear_ButDifferentProviderIds_IsNotADuplicate()
    {
        var a = Make.Item("Heat", 1995, ids: ("Tmdb", "949"));
        var b = Make.Item("Heat", 1995, ids: ("Tmdb", "123456"));
        Assert.Empty(DuplicateDetector.Find(new[] { a, b }));
    }

    [Fact]
    public void DifferentYears_AreNotGroupedByTitle()
    {
        var a = Make.Item("Dune", 1984);
        var b = Make.Item("Dune", 2021);
        Assert.Empty(DuplicateDetector.Find(new[] { a, b }));
    }

    [Fact]
    public void MoviesAndSeries_AreNeverMixed_AndVirtualItemsAreIgnored()
    {
        var a = Make.Item("Fargo", 1996, MediaKind.Movie, ids: ("Imdb", "tt0116282"));
        var b = Make.Item("Fargo", 1996, MediaKind.Series, ids: ("Imdb", "tt0116282"));
        var c = Make.Item("Fargo", 1996, MediaKind.Movie, ids: ("Imdb", "tt0116282"));
        c.IsVirtual = true;
        Assert.Empty(DuplicateDetector.Find(new[] { a, b, c }));
    }

    [Fact]
    public void ProviderIdInFolderName_Counts()
    {
        var a = Make.Item("Matrix", 1999, path: "/a/The Matrix (1999) [tmdbid-603]");
        var b = Make.Item("The Matrix Reloaded", 2003, ids: ("Tmdb", "603"));
        var g = Assert.Single(DuplicateDetector.Find(new[] { a, b }));
        Assert.Contains(DuplicateSignal.ProviderId, g.Signals);
    }

    [Fact]
    public void ThreeCopies_ChainIntoOneGroup()
    {
        var a = Make.Item("Show", 2010, MediaKind.Series, ids: ("Tvdb", "1"));
        var b = Make.Item("Show", 2010, MediaKind.Series, "/m", ("Tvdb", "1"), ("Imdb", "tt9"));
        var c = Make.Item("Show!", 2011, MediaKind.Series, ids: ("Imdb", "tt9"));
        var g = Assert.Single(DuplicateDetector.Find(new[] { a, b, c }));
        Assert.Equal(3, g.ItemIds.Count);
        Assert.Equal(DuplicateDetector.GroupId(new[] { c.Id, a.Id, b.Id }), DuplicateDetector.GroupId(g.ItemIds));
    }

    [Fact]
    public void EpisodeDuplicatesInsideOneSeries_AreFound()
    {
        var e1 = Make.Episode(1, 1);
        var e1b = Make.Episode(1, 1);
        var e2 = Make.Episode(1, 2);
        var g = Assert.Single(DuplicateDetector.FindEpisodeDuplicates(new[] { e1, e1b, e2 }));
        Assert.Equal(MediaKind.Episode, g.Kind);
        Assert.Contains("S01E01", g.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EpisodeOverlap_CountsSharedOnlyAndMissing_IncludingMultiEpisodeFiles()
    {
        var a = new HashSet<string>(new[] { Make.Episode(1, 1), Make.Episode(1, 2, 3), Make.Episode(1, 4) }.SelectMany(EpisodeOverlap.Keys));
        var b = new HashSet<string>(new[] { Make.Episode(1, 1), Make.Episode(1, 2), Make.Episode(0, 1) }.SelectMany(EpisodeOverlap.Keys));
        var ida = Guid.NewGuid();
        var idb = Guid.NewGuid();
        var cov = EpisodeOverlap.Compute(new Dictionary<Guid, HashSet<string>> { [ida] = a, [idb] = b }).ToDictionary(c => c.ItemId);

        Assert.Equal(4, cov[ida].EpisodeCount);
        Assert.Equal(2, cov[idb].EpisodeCount);
        Assert.Equal(2, cov[ida].SharedWithAll);
        Assert.Equal(2, cov[ida].OnlyHere);
        Assert.Equal(new[] { "S01E03", "S01E04" }, cov[ida].OnlyHereSample);
        Assert.Equal(0, cov[idb].OnlyHere);
        Assert.Equal(2, cov[idb].MissingHere);
    }
}

public class ScoringTests
{
    private static DuplicateCopy Copy(ItemSnapshot item, int episodes = 0, bool exists = true, bool? managed = null)
        => new()
        {
            Item = item,
            PathExists = exists,
            FileCount = Math.Max(1, episodes),
            EpisodeCount = episodes,
            ArrManaged = managed,
            ArrService = item.Kind == MediaKind.Series ? "Sonarr" : "Radarr",
            ArrPath = managed == true ? item.Path : "/tv/other"
        };

    private static DuplicateGroup Group(MediaKind kind, params DuplicateCopy[] copies)
        => new() { Kind = kind, Copies = copies.ToList(), Signals = { DuplicateSignal.ProviderId } };

    [Fact]
    public void ManagedAndCompleteCopy_IsKept_WithPlainEnglishReasons()
    {
        var stale = Make.Item("Show", 2015, MediaKind.Series, "/data/8tb1/TV/Show", ("Tvdb", "1"));
        var current = Make.Item("Show", 2015, MediaKind.Series, "/data/8tb2/TV/Show", ("Tvdb", "1"));
        var g = Group(MediaKind.Series, Copy(stale, 18, managed: false), Copy(current, 24, managed: true));
        g.Copies[0].ExpectedEpisodes = 24;
        g.Copies[1].ExpectedEpisodes = 24;

        DuplicateScorer.Score(g, new ScoringContext { ArrAvailable = true, ArrName = "Sonarr" });

        Assert.Equal(current.Id, g.RecommendedKeepId);
        Assert.Equal(Confidence.High, g.Confidence);
        Assert.True(g.AutoSelect);
        var keep = g.Copies.Single(c => c.Decision == "Keep");
        var remove = g.Copies.Single(c => c.Decision == "Remove");
        Assert.Contains(keep.Reasons, r => r.StartsWith("path matches Sonarr's path", StringComparison.Ordinal));
        Assert.Contains(keep.Reasons, r => r.StartsWith("has 24/24 episodes", StringComparison.Ordinal));
        Assert.Contains(remove.Reasons, r => r.StartsWith("has 18/24 episodes", StringComparison.Ordinal));
        Assert.Contains(remove.Reasons, r => r == "not the path Sonarr manages");
        Assert.Contains(DuplicateScorer.FreeSpaceNote, g.Notes);
    }

    [Fact]
    public void HigherResolution_Wins_ButFileSizeIsNotUsed()
    {
        var big = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        big.Width = 1920;
        big.Height = 1080;
        big.SizeBytes = 60_000_000_000;
        var uhd = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        uhd.Width = 3840;
        uhd.Height = 2160;
        uhd.SizeBytes = 20_000_000_000;
        uhd.VideoCodec = "hevc";
        uhd.VideoRange = "HDR10";
        var g = Group(MediaKind.Movie, Copy(big), Copy(uhd));
        DuplicateScorer.Score(g, new ScoringContext { ArrAvailable = false });

        Assert.Equal(uhd.Id, g.RecommendedKeepId);
        var keep = g.Copies.Single(c => c.Item.Id == uhd.Id);
        Assert.Contains(keep.Reasons, r => r == "highest resolution (2160p HEVC HDR10)");
        Assert.Contains(g.Copies.Single(c => c.Item.Id == big.Id).Reasons, r => r == "lower resolution (1080p vs 2160p)");
        Assert.Contains(g.Notes, n => n.Contains("not configured", StringComparison.Ordinal));
    }

    [Fact]
    public void NearlyEqualCopies_AreLowConfidence_AndNotAutoSelected()
    {
        var a = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var b = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        b.DateCreated = a.DateCreated.AddDays(1);
        var g = Group(MediaKind.Movie, Copy(a), Copy(b));
        DuplicateScorer.Score(g, new ScoringContext());

        Assert.Equal(Confidence.Low, g.Confidence);
        Assert.False(g.AutoSelect);
        Assert.Contains("Not auto-selected", g.ConfidenceReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleOnlyMatch_IsAtMostMedium()
    {
        var a = Make.Item("Film", 2001, path: "/a");
        a.Width = 3840;
        a.Height = 2160;
        var b = Make.Item("Film", 2001, path: "/b");
        b.Width = 640;
        b.Height = 360;
        var g = new DuplicateGroup { Kind = MediaKind.Movie, Copies = { Copy(a), Copy(b, exists: false) }, Signals = { DuplicateSignal.TitleYear } };
        DuplicateScorer.Score(g, new ScoringContext());
        Assert.Equal(Confidence.Medium, g.Confidence);
        Assert.True(g.AutoSelect);
    }

    [Fact]
    public void BestCopyNotOnDisk_IsLow()
    {
        var a = Make.Item("Show", 2015, MediaKind.Series, "/x", ("Tvdb", "1"));
        var b = Make.Item("Show", 2015, MediaKind.Series, "/y", ("Tvdb", "1"));
        var g = Group(MediaKind.Series, Copy(a, 24, exists: false, managed: true), Copy(b, 2, managed: false));
        DuplicateScorer.Score(g, new ScoringContext { ArrAvailable = true });
        Assert.Equal(a.Id, g.RecommendedKeepId);
        Assert.Equal(Confidence.Low, g.Confidence);
        Assert.Contains("not on disk", g.ConfidenceReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFiles_ForceLowConfidence()
    {
        var a = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var b = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var g = Group(MediaKind.Movie, Copy(a), Copy(b));
        g.Copies[1].SharesFilesWithAnotherCopy = true;
        DuplicateScorer.Score(g, new ScoringContext());
        Assert.Equal(Confidence.Low, g.Confidence);
    }

    [Fact]
    public void Override_SwapsDecisions_AndWarnsAboutManagedCopyAndWatchData()
    {
        var stale = Make.Item("Show", 2015, MediaKind.Series, "/stale", ("Tvdb", "1"));
        var current = Make.Item("Show", 2015, MediaKind.Series, "/current", ("Tvdb", "1"));
        var cs = Copy(stale, 18, managed: false);
        var cc = Copy(current, 24, managed: true);
        cc.Watches.Add(new CopyWatch { UserId = Guid.NewGuid(), UserName = "sam", PlayedCount = 5 });
        cc.Coverage = new EpisodeCoverage { ItemId = current.Id, OnlyHere = 6, OnlyHereSample = { "S02E01" } };
        var g = Group(MediaKind.Series, cs, cc);
        var ctx = new ScoringContext { ArrAvailable = true, ArrName = "Sonarr" };
        DuplicateScorer.Score(g, ctx);
        Assert.Equal(current.Id, g.KeepId);

        DuplicateScorer.Evaluate(g, stale.Id, ctx);
        Assert.True(g.Overridden);
        var removed = g.Copies.Single(c => c.Item.Id == current.Id);
        Assert.Equal("Remove", removed.Decision);
        Assert.True(removed.Recommended);
        Assert.Contains(removed.Warnings, w => w.Contains("This is the copy Sonarr manages", StringComparison.Ordinal));
        Assert.Contains(removed.Warnings, w => w.Contains("6 episode(s) the kept copy does not have", StringComparison.Ordinal));
        Assert.Contains(removed.Warnings, w => w.Contains("sam", StringComparison.Ordinal));
        var kept = g.Copies.Single(c => c.Item.Id == stale.Id);
        Assert.Contains(kept.Warnings, w => w.StartsWith("You chose this copy instead of the recommendation", StringComparison.Ordinal));
        Assert.Equal(kept.Factors.Count, kept.Reasons.Count);
    }

    [Fact]
    public void Evaluate_RejectsUnknownKeeper()
    {
        var g = Group(MediaKind.Movie, Copy(Make.Item("a", 1)), Copy(Make.Item("a", 1)));
        DuplicateScorer.Score(g, new ScoringContext());
        Assert.Throws<ToolkitException>(() => DuplicateScorer.Evaluate(g, Guid.NewGuid(), new ScoringContext()));
    }

    [Fact]
    public void Weights_AreAdjustable_PreferredDriveCanDecide()
    {
        var a = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var b = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        b.DateCreated = a.DateCreated.AddDays(3);
        var ca = Copy(a);
        var cb = Copy(b);
        cb.Drive = "8TB 2";
        var g = Group(MediaKind.Movie, ca, cb);
        var w = new ScoreWeights { PreferredDrive = 50 };
        DuplicateScorer.Score(g, new ScoringContext { Weights = w, PreferredDrive = "8TB 2" });
        Assert.Equal(b.Id, g.RecommendedKeepId);
        Assert.Contains(cb.Reasons, r => r == "on the preferred drive \"8TB 2\"");
        Assert.Contains(ca.Reasons, r => r == "added first (2020-01-01)");
    }

    [Fact]
    public void WatchedCopy_GetsWatchPoints()
    {
        var a = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var b = Make.Item("Film", 2001, ids: ("Tmdb", "5"));
        var cb = Copy(b);
        cb.Watches.Add(new CopyWatch { UserName = "alex", PlayedCount = 1 });
        var g = Group(MediaKind.Movie, Copy(a), cb);
        DuplicateScorer.Score(g, new ScoringContext());
        Assert.Contains(cb.Reasons, r => r == "watched by alex");
        Assert.True(cb.Score > g.Copies[0].Score - 3);
    }

    [Fact]
    public void Configuration_NormalizeClampsWeights()
    {
        var c = new PluginConfiguration { Weights = new ScoreWeights { ManagedPath = -5, MediumConfidenceGap = 500, HighConfidenceGap = 40 } };
        Assert.True(c.Normalize());
        Assert.Equal(0, c.Weights.ManagedPath);
        Assert.Equal(40, c.Weights.MediumConfidenceGap);
        Assert.True(new PluginConfiguration().SafeMode);
        Assert.True(new PluginConfiguration().DryRunDefault);
        Assert.False(new PluginConfiguration().ScheduledScansEnabled);
    }
}
