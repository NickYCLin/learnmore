using System.Text.RegularExpressions;

namespace LearnMore.Services;

public static class LyricLineFilter
{
    private static readonly Regex NonLyricNoiseRegex = new(
        @"^\s*[\[\(（【]?\s*(?:音楽|音樂|音乐|拍手|笑い|歓声|歌|唄|applause|laughter|music)\s*[\]\)）】]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreditLineRegex = new(
        @"^\s*(?:作[词詞]|作曲|[制製]作人?|[制製]作|编曲|編曲|词曲|詞曲|填[词詞]|曲|词|詞|編?譜|编?谱|arrange(?:ment)?|arranged\s+by|lyrics?|lyricist|composer|music|producer|produced\s+by|vocal|歌|唄|原唱|演唱)\s*[:：／/\-]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SocialPromptLineRegex = new(
        @"^\s*(?=.*(?:サブタイトル|字幕|subtitle|subtitles|caption|captions))(?=.*(?:フォロー|追蹤|追踪|follow|instagram|インスタグラム|ig\b)).+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ShouldSkipSyncedLyricLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = text.Trim();
        if (NonLyricNoiseRegex.IsMatch(normalized))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"^\(.*\)$"))
        {
            return true;
        }

        return CreditLineRegex.IsMatch(normalized)
               || SocialPromptLineRegex.IsMatch(normalized);
    }
}
