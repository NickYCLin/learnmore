using System.Globalization;
using System.Text.RegularExpressions;
using LearnMore.Models;

namespace LearnMore.Services;

public class YouTubeSubtitleParserService
{
    private static readonly Regex TimestampRegex = new(@"^(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})", RegexOptions.Compiled);
    private static readonly Regex InlineTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex InlineNoiseRegex = new(@"(?:\[(?:音楽|拍手|笑い|歓声)\]|【(?:音楽|拍手|笑い|歓声)】)", RegexOptions.Compiled);
    private static readonly Regex ObservedLowConfidenceHallucinationRegex = new(@"(?:ちょんちん|ちょん頑張って|頑張ってドンピン|ドンピン|頑張って。ちょん|ちょん|やつちょ|かずお|ボンボ|チャンスてて|ももう1|ちゃ当たるもう1回はいはいもう1回|ハに)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObservedLongTailHallucinationSuffixRegex = new(@"ちょ頑張っていただかないと10$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObservedLongSentenceHallucinationRegex = new(@"(?:めどめんの早くない|諦めてなれちゅ)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public List<LyricSegment> ParseSegments(IEnumerable<string> lines)
    {
        var segments = new List<LyricSegment>();
        var blocks = Regex.Split(string.Join("\n", lines), @"\n\n+");
        string prevText = string.Empty;

        foreach (var block in blocks)
        {
            var blockLines = block.Split('\n');
            Match? tsMatch = null;
            foreach (var blockLine in blockLines)
            {
                var match = TimestampRegex.Match(blockLine);
                if (match.Success)
                {
                    tsMatch = match;
                    break;
                }
            }

            if (tsMatch is null)
            {
                continue;
            }

            double startSec = ParseTimestamp(tsMatch.Groups[1].Value);
            double endSec = ParseTimestamp(tsMatch.Groups[2].Value);
            if (endSec - startSec < 0.1)
            {
                continue;
            }

            var textLines = blockLines
                .Where(blockLine => !TimestampRegex.IsMatch(blockLine)
                    && !blockLine.StartsWith("WEBVTT")
                    && !blockLine.StartsWith("Kind:")
                    && !blockLine.StartsWith("Language:"))
                .Select(NormalizeCueText)
                .Where(blockLine => !string.IsNullOrWhiteSpace(blockLine))
                .ToList();

            if (textLines.Count == 0)
            {
                continue;
            }

            var newText = textLines[^1];
            if (!ShouldKeepSubtitleSegment(newText))
            {
                prevText = string.Empty;
                continue;
            }

            if (newText == prevText)
            {
                continue;
            }

            prevText = newText;
            segments.Add(new LyricSegment
            {
                TimeStamp = startSec,
                Japanese = newText
            });
        }

        return segments;
    }

    private static string NormalizeCueText(string blockLine)
    {
        var normalized = InlineTagRegex.Replace(blockLine, string.Empty);
        normalized = InlineNoiseRegex.Replace(normalized, string.Empty);
        normalized = normalized.Replace("♪", string.Empty).Replace("♫", string.Empty);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        normalized = normalized.Trim('"', '\'', '“', '”', '『', '』', '「', '」', '【', '】', '[', ']', '(', ')');
        normalized = normalized.Trim(' ', '　', '、', '。', '，', ',', '！', '!', '？', '?', '・');
        normalized = TrimObservedLongTailHallucinationSuffix(normalized);
        return normalized.Trim();
    }

    private static bool ShouldKeepSubtitleSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var substantive = Regex.Replace(text, @"[\p{P}\p{S}\s]", string.Empty);
        if (substantive.Length < 2 && !Regex.IsMatch(substantive, @"[\p{IsCJKUnifiedIdeographs}ぁ-んァ-ンー々]", RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (Regex.IsMatch(substantive, @"^\d+$"))
        {
            return false;
        }

        if (Regex.IsMatch(text, @"^[A-Za-z]$"))
        {
            return false;
        }

        if (Regex.IsMatch(text, @"(?:9876|8765|7654|6543|5432|4321|3210)\d*$")
            && Regex.IsMatch(text, @"(?:秒数|数え|カウント)", RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (ObservedLowConfidenceHallucinationRegex.IsMatch(text))
        {
            return false;
        }

        if (text.Length >= 40 && ObservedLongSentenceHallucinationRegex.IsMatch(text))
        {
            return false;
        }

        return true;
    }

    private static string TrimObservedLongTailHallucinationSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = ObservedLongTailHallucinationSuffixRegex.Replace(text, string.Empty).Trim();
        return trimmed;
    }

    private static double ParseTimestamp(string timestamp)
    {
        var parts = timestamp.Split(':');
        return int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600
               + int.Parse(parts[1], CultureInfo.InvariantCulture) * 60
               + double.Parse(parts[2], CultureInfo.InvariantCulture);
    }
}
