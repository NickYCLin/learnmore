using System.Text.RegularExpressions;

namespace LearnMore.Services;

public static class SyncedLyricsMetadataMatcher
{
    public static IReadOnlyList<string> BuildTitleCandidates(string title)
    {
        var candidates = new List<string>();
        Add(title);

        var withoutParentheses = Regex
            .Replace(title, @"[\(\（][^\)\）]*[\)\）]", " ")
            .Trim();
        Add(withoutParentheses);

        var slashIndex = withoutParentheses.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex > 0)
        {
            Add(withoutParentheses[..slashIndex]);
        }

        var colonIndex = withoutParentheses.IndexOfAny([':', '：']);
        if (colonIndex > 0)
        {
            Add(withoutParentheses[..colonIndex]);
        }

        return candidates;

        void Add(string? value)
        {
            var normalized = NormalizeSpaces(value);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(normalized);
            }
        }
    }

    public static bool IsLikelyTrackMatch(string? candidateTrackName, string requestedTitle)
    {
        var candidate = NormalizeForMatch(candidateTrackName);
        var requestedCandidates = BuildTitleCandidates(requestedTitle)
            .Select(NormalizeForMatch)
            .Where(value => value.Length > 0)
            .ToList();

        return candidate.Length > 0
            && requestedCandidates.Any(requested =>
                string.Equals(candidate, requested, StringComparison.Ordinal)
                || requested.StartsWith(candidate, StringComparison.Ordinal)
                || candidate.StartsWith(requested, StringComparison.Ordinal)
                || (requested.Length >= 4 && candidate.Contains(requested, StringComparison.Ordinal)));
    }

    public static bool IsLikelyArtistMatch(string? candidateArtistName, string requestedArtist)
    {
        if (string.IsNullOrWhiteSpace(requestedArtist))
        {
            return true;
        }

        var candidate = NormalizeForMatch(candidateArtistName);
        var requested = NormalizeForMatch(requestedArtist);
        return candidate.Length > 0
            && requested.Length > 0
            && (candidate.Contains(requested, StringComparison.Ordinal)
                || requested.Contains(candidate, StringComparison.Ordinal));
    }

    private static string NormalizeSpaces(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeForMatch(string? value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"[\(\（][^\)\）]*[\)\）]", string.Empty);
        normalized = Regex.Replace(normalized, @"\b(MUSIC\s*VIDEO|MV|OFFICIAL|LYRIC\s*VIDEO)\b", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", string.Empty);
        return normalized.ToLowerInvariant();
    }
}
