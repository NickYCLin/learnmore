using System.Text.RegularExpressions;

namespace LearnMore.Services;

public static class PerformerNameNormalizer
{
    private static readonly Dictionary<string, string> CollectionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["米津玄師 Kenshi Yonezu"] = "米津玄師",
        ["tuki.(17)"] = "tuki."
    };

    public static string NormalizeForCollection(string? performer)
    {
        if (string.IsNullOrWhiteSpace(performer))
        {
            return string.Empty;
        }

        string normalized = Regex.Replace(performer.Trim(), @"\s+", " ");
        return CollectionAliases.TryGetValue(normalized, out string? canonical)
            ? canonical
            : normalized;
    }

    public static IReadOnlyList<string> GetCollectionAliases(string? performer)
    {
        string canonical = NormalizeForCollection(performer);
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return Array.Empty<string>();
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
        foreach (var pair in CollectionAliases)
        {
            if (string.Equals(pair.Value, canonical, StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(pair.Key);
            }
        }

        return aliases.ToList();
    }
}
