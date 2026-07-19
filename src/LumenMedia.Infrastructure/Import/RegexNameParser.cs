using System.Text.RegularExpressions;
using LumenMedia.Application.Abstractions;
using LumenMedia.Domain.Enums;

namespace LumenMedia.Infrastructure.Import;

/// <summary>
/// Heuristic release-name parser (inspired by guessit/scene naming). Extracts title, year,
/// season/episode, quality, codec and release group. Pure and fully unit-tested
/// (see import-pipeline.md §3). No I/O.
/// </summary>
public sealed partial class RegexNameParser : INameParser
{
    public ParsedName Parse(string fileName)
    {
        var name = StripExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
            return new ParsedName { Kind = MediaKind.Movie, Title = "Unknown" };

        var quality = NormalizeQuality(QualityRegex().Match(name).Value);
        var codec = NullIfEmpty(CodecRegex().Match(name).Value.ToLowerInvariant());
        var releaseGroup = ExtractReleaseGroup(name);

        // ---- Series detection ----
        var se = SeasonEpisodeRegex().Match(name);
        if (se.Success)
        {
            return new ParsedName
            {
                Kind = MediaKind.Series,
                Title = CleanTitle(name[..se.Index]),
                Year = ExtractYear(name),
                Season = int.Parse(se.Groups[1].Value),
                Episode = int.Parse(se.Groups[2].Value),
                Quality = quality,
                Codec = codec,
                ReleaseGroup = releaseGroup,
            };
        }

        var seWord = SeasonWordRegex().Match(name);
        if (seWord.Success)
        {
            return new ParsedName
            {
                Kind = MediaKind.Series,
                Title = CleanTitle(name[..seWord.Index]),
                Year = ExtractYear(name),
                Season = int.Parse(seWord.Groups[1].Value),
                Episode = int.Parse(seWord.Groups[2].Value),
                Quality = quality,
                Codec = codec,
                ReleaseGroup = releaseGroup,
            };
        }

        var altEp = AltEpisodeRegex().Match(name);
        if (altEp.Success)
        {
            return new ParsedName
            {
                Kind = MediaKind.Series,
                Title = CleanTitle(name[..altEp.Index]),
                Year = ExtractYear(name),
                Season = int.Parse(altEp.Groups[1].Value),
                Episode = int.Parse(altEp.Groups[2].Value),
                Quality = quality,
                Codec = codec,
                ReleaseGroup = releaseGroup,
            };
        }

        // ---- Movie ----
        var cut = FindTitleCut(name);
        var title = cut > 0 ? CleanTitle(name[..cut]) : CleanTitle(name);
        if (string.IsNullOrWhiteSpace(title))
            title = "Unknown";

        return new ParsedName
        {
            Kind = MediaKind.Movie,
            Title = title,
            Year = ExtractYear(name),
            Quality = quality,
            Codec = codec,
            ReleaseGroup = releaseGroup,
        };
    }

    private static string StripExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;
        var name = fileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        // Only treat a short trailing token as an extension.
        if (dot > 0 && name.Length - dot <= 5)
            name = name[..dot];
        return name;
    }

    private static int? ExtractYear(string name)
    {
        // Prefer a year wrapped in parentheses/brackets.
        var paren = ParenYearRegex().Match(name);
        if (paren.Success)
            return int.Parse(paren.Groups[1].Value);
        var m = YearRegex().Match(name);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static int FindTitleCut(string name)
    {
        var indices = new List<int>();
        void Add(Match m)
        {
            if (m.Success && m.Index > 0)
                indices.Add(m.Index);
        }

        Add(YearRegex().Match(name));
        Add(QualityRegex().Match(name));
        Add(SourceTagRegex().Match(name));
        Add(CodecRegex().Match(name));

        return indices.Count > 0 ? indices.Min() : name.Length;
    }

    private static string CleanTitle(string raw)
    {
        var text = raw.Replace('.', ' ').Replace('_', ' ');
        text = SeparatorRegex().Replace(text, " ");
        // Drop leftover opening bracket/paren fragments and trailing punctuation.
        text = text.Trim().Trim('-', '(', '[', '{', ' ', ':', '_');
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    private static string? ExtractReleaseGroup(string name)
    {
        var m = ReleaseGroupRegex().Match(name);
        if (!m.Success)
            return null;
        var group = m.Groups[1].Value;
        // Filter out common source suffixes that look like a group (e.g. WEB-DL).
        string[] notGroups = ["DL", "WEB", "RIP", "HD", "TV"];
        return notGroups.Contains(group.ToUpperInvariant()) ? null : group;
    }

    private static string? NormalizeQuality(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Equals("4K", StringComparison.OrdinalIgnoreCase) ? "2160p" : value.ToLowerInvariant();
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"[Ss](\d{1,2})[\s._-]?[Ee](\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"[Ss]eason[\s._-]?(\d{1,2})[\s._-]?(?:[Ee]pisode|[Ee])[\s._-]?(\d{1,3})")]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})x(\d{1,3})(?!\d)")]
    private static partial Regex AltEpisodeRegex();

    [GeneratedRegex(@"[(\[](19\d{2}|20\d{2})[)\]]")]
    private static partial Regex ParenYearRegex();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\b(2160p|1080p|720p|480p|4K)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\b(x264|x265|h\.?264|h\.?265|hevc|avc|xvid|divx)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(BluRay|BDRip|BRRip|WEB-?DL|WEBRip|HDTV|DVDRip|REMUX|PROPER|REPACK)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceTagRegex();

    [GeneratedRegex(@"-([A-Za-z0-9]{2,})$")]
    private static partial Regex ReleaseGroupRegex();

    [GeneratedRegex(@"[\s._-]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
