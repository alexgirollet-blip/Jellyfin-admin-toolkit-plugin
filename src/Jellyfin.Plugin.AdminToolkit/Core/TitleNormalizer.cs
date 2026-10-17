using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AdminToolkit.Core;

/// <summary>Result of normalising a title: a comparison key and the year found in the title (if any).</summary>
public sealed record NormalizedTitle(string Key, int? Year);

/// <summary>
/// Normalises titles so "The Office (US) (2005)", "Office, The [2005] 1080p WEB-DL" and "the office us" compare equal.
/// Used only for the "likely" duplicate signal; provider ids are always the stronger signal.
/// </summary>
public static partial class TitleNormalizer
{
    // Only unambiguous technical tags: words that also appear in real titles ("Complete", "Limited", "Web") are kept.
    private static readonly string[] ReleaseTags =
    {
        "2160p", "1080p", "1080i", "720p", "576p", "480p", "4k", "uhd",
        "bluray", "blu ray", "bdrip", "brrip", "bdremux", "remux", "web dl", "webdl", "webrip", "hdtv", "dvdrip", "hdrip",
        "x264", "x265", "h264", "h 264", "h265", "h 265", "hevc", "xvid", "divx", "10bit",
        "hdr10", "hdr10plus", "dovi", "truehd", "dts hd", "ddp5 1", "dd5 1", "proper", "repack"
    };

    public static NormalizedTitle Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new NormalizedTitle(string.Empty, null);
        }

        var s = RemoveDiacritics(title).ToLowerInvariant();

        // Jellyfin naming tags such as [tmdbid-123], {imdb-tt123}, [tvdbid=81189].
        s = ProviderTag().Replace(s, " ");

        int? year = null;
        var ym = BracketYear().Match(s);
        if (ym.Success)
        {
            year = int.Parse(ym.Groups[1].Value, CultureInfo.InvariantCulture);
            s = BracketYear().Replace(s, " ");
        }

        // Remaining bracketed text is usually release info ("[1080p]", "(Remastered)"), except country tags like "(US)".
        s = Brackets().Replace(s, m => IsCountryTag(m.Groups[1].Value) ? " " + m.Groups[1].Value + " " : " ");

        s = s.Replace("&", " and ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal);
        s = NonAlnum().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();

        foreach (var tag in ReleaseTags.OrderByDescending(t => t.Length))
        {
            s = Regex.Replace(s, @"(?<=^|\s)" + Regex.Escape(tag) + @"(?=\s|$)", " ", RegexOptions.CultureInvariant);
        }

        s = Spaces().Replace(s, " ").Trim();
        s = LeadingArticle().Replace(s, string.Empty);
        s = TrailingArticle().Replace(s, string.Empty);
        s = s.Replace(" ", string.Empty, StringComparison.Ordinal);
        return new NormalizedTitle(s, year);
    }

    /// <summary>Extracts provider ids written into a folder or file name (Jellyfin naming: "[tmdbid-603]", "{imdb-tt0133093}").</summary>
    public static System.Collections.Generic.Dictionary<string, string> ProviderIdsFromName(string? name)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(name))
        {
            return result;
        }

        foreach (Match m in ProviderTag().Matches(name.ToLowerInvariant()))
        {
            var key = m.Groups[1].Value switch
            {
                "tmdb" or "tmdbid" => "Tmdb",
                "tvdb" or "tvdbid" => "Tvdb",
                "imdb" or "imdbid" => "Imdb",
                _ => null
            };
            if (key is not null)
            {
                result[key] = m.Groups[2].Value;
            }
        }

        return result;
    }

    private static bool IsCountryTag(string s) => s.Trim() is "us" or "uk" or "au" or "nz" or "ca" or "fr" or "de" or "jp" or "kr" or "es" or "it";

    private static string RemoveDiacritics(string text)
    {
        var formD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[\[\{]\s*(tmdbid|tmdb|tvdbid|tvdb|imdbid|imdb)\s*[-=]\s*([a-z0-9]+)\s*[\]\}]", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderTag();

    [GeneratedRegex(@"[\(\[]\s*((?:19|20)\d{2})\s*[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketYear();

    [GeneratedRegex(@"[\(\[\{]([^\)\]\}]*)[\)\]\}]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(the|a|an)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"\s+(the|a|an)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingArticle();
}
