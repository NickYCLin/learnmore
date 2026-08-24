using System.Net;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using NMeCab;
using NMeCab.Specialized;

namespace LearnMore.Services
{
    public sealed class JapaneseRubyGeneratorService
    {
        private readonly IWebHostEnvironment _env;
        private readonly Lazy<MeCabIpaDicTagger> _tagger;
        private readonly Lazy<List<RubyOverrideEntry>> _overrides;

        public JapaneseRubyGeneratorService(IWebHostEnvironment env)
        {
            _env = env;
            _tagger = new Lazy<MeCabIpaDicTagger>(CreateTagger);
            _overrides = new Lazy<List<RubyOverrideEntry>>(LoadOverrides);
        }

        public string ConvertToRubyHtml(string text)
        {
            var tokens = Tokenize(text);
            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var token in tokens)
            {
                if (!string.IsNullOrWhiteSpace(token.RubyHtml))
                {
                    builder.Append(token.RubyHtml);
                    continue;
                }

                if (!ContainsKanji(token.Surface) || string.IsNullOrWhiteSpace(token.Reading) || token.Reading == "*")
                {
                    builder.Append(WebUtility.HtmlEncode(token.Surface));
                    continue;
                }

                builder.Append(AnnotateMixedToken(token.Surface, token.Reading));
            }

            return JapaneseRubySanitizer.NormalizeRubyHtml(builder.ToString());
        }

        public IReadOnlyList<JapaneseReadingToken> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<JapaneseReadingToken>();
            }

            var tokens = new List<JapaneseReadingToken>();
            int index = 0;

            while (index < text.Length)
            {
                var matched = TryMatchOverride(text, index);
                if (matched != null)
                {
                    tokens.Add(new JapaneseReadingToken(matched.Text, matched.Reading, matched.Ruby));
                    index += matched.Text.Length;
                    continue;
                }

                var numeric = JapaneseNumericReading.TryMatch(text, index);
                if (numeric != null)
                {
                    tokens.Add(new JapaneseReadingToken(numeric.Surface, numeric.Reading, numeric.RubyHtml));
                    index += numeric.Surface.Length;
                    continue;
                }

                int nextSpecialIndex = FindNextSpecialStart(text, index);
                string chunk = nextSpecialIndex >= 0
                    ? text[index..nextSpecialIndex]
                    : text[index..];

                tokens.AddRange(TokenizeChunkWithMeCab(chunk));
                index += chunk.Length;
            }

            ApplyReadingHeuristics(tokens);
            return tokens;
        }

        private IEnumerable<JapaneseReadingToken> TokenizeChunkWithMeCab(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                yield break;
            }

            var cursor = 0;
            foreach (var node in _tagger.Value.Parse(chunk))
            {
                if (node.Stat == MeCabNodeStat.Bos || node.Stat == MeCabNodeStat.Eos)
                {
                    continue;
                }

                var surface = node.Surface ?? string.Empty;
                if (string.IsNullOrEmpty(surface))
                {
                    continue;
                }

                var foundIndex = chunk.IndexOf(surface, cursor, StringComparison.Ordinal);
                if (foundIndex > cursor)
                {
                    yield return new JapaneseReadingToken(chunk[cursor..foundIndex], string.Empty);
                }

                var token = ConvertNodeToToken(node);
                if (token != null)
                {
                    yield return token;
                }

                cursor = foundIndex >= cursor
                    ? foundIndex + surface.Length
                    : Math.Min(chunk.Length, cursor + surface.Length);
            }

            if (cursor < chunk.Length)
            {
                yield return new JapaneseReadingToken(chunk[cursor..], string.Empty);
            }
        }

        private JapaneseReadingToken? ConvertNodeToToken(MeCabIpaDicNode node)
        {
            string surface = node.Surface ?? string.Empty;
            if (string.IsNullOrEmpty(surface))
            {
                return null;
            }

            if (!ContainsJapanese(surface))
            {
                return new JapaneseReadingToken(surface, string.Empty);
            }

            string reading = ContainsKanji(surface)
                ? ToHiragana(node.Reading)
                : ToHiragana(surface);

            if (string.IsNullOrWhiteSpace(reading) || reading == "*")
            {
                return new JapaneseReadingToken(surface, string.Empty);
            }

            return new JapaneseReadingToken(surface, reading);
        }

        private string AnnotateMixedToken(string surface, string reading)
        {
            if (string.IsNullOrEmpty(surface))
            {
                return string.Empty;
            }

            if (!ContainsKanji(surface))
            {
                return WebUtility.HtmlEncode(surface);
            }

            if (string.IsNullOrEmpty(reading))
            {
                return WebUtility.HtmlEncode(surface);
            }

            if (IsAllKanji(surface))
            {
                return WrapRuby(surface, reading);
            }

            var leadingKana = GetLeadingKana(surface);
            if (leadingKana.Length > 0)
            {
                string normalizedLeading = ToHiragana(leadingKana);
                if (reading.StartsWith(normalizedLeading, StringComparison.Ordinal))
                {
                    return WebUtility.HtmlEncode(leadingKana) +
                           AnnotateMixedToken(surface[leadingKana.Length..], reading[normalizedLeading.Length..]);
                }
            }

            var trailingKana = GetTrailingKana(surface);
            if (trailingKana.Length > 0)
            {
                string normalizedTrailing = ToHiragana(trailingKana);
                if (reading.EndsWith(normalizedTrailing, StringComparison.Ordinal))
                {
                    string coreSurface = surface[..^trailingKana.Length];
                    string coreReading = reading[..^normalizedTrailing.Length];
                    return AnnotateMixedToken(coreSurface, coreReading) + WebUtility.HtmlEncode(trailingKana);
                }
            }

            foreach (var kanaRun in GetInternalKanaRuns(surface))
            {
                string kana = kanaRun.Text;
                string normalizedKana = ToHiragana(kana);
                int matchIndex = reading.IndexOf(normalizedKana, StringComparison.Ordinal);
                if (matchIndex < 0)
                {
                    continue;
                }

                int secondMatch = reading.IndexOf(normalizedKana, matchIndex + normalizedKana.Length, StringComparison.Ordinal);
                if (secondMatch >= 0)
                {
                    continue;
                }

                string leftSurface = surface[..kanaRun.Start];
                string rightSurface = surface[(kanaRun.Start + kanaRun.Length)..];
                string leftReading = reading[..matchIndex];
                string rightReading = reading[(matchIndex + normalizedKana.Length)..];

                return AnnotateMixedToken(leftSurface, leftReading) +
                       WebUtility.HtmlEncode(kana) +
                       AnnotateMixedToken(rightSurface, rightReading);
            }

            return WrapRuby(surface, reading);
        }

        private RubyOverrideEntry? TryMatchOverride(string text, int startIndex)
        {
            foreach (var entry in _overrides.Value)
            {
                if (startIndex + entry.Text.Length > text.Length)
                {
                    continue;
                }

                if (text.AsSpan(startIndex, entry.Text.Length).SequenceEqual(entry.Text))
                {
                    return entry;
                }
            }

            return null;
        }

        private int FindNextSpecialStart(string text, int startIndex)
        {
            int overrideIndex = FindNextOverrideStart(text, startIndex);
            int numericIndex = JapaneseNumericReading.FindNextStart(text, startIndex);

            return (overrideIndex, numericIndex) switch
            {
                (< 0, < 0) => -1,
                (< 0, _) => numericIndex,
                (_, < 0) => overrideIndex,
                _ => Math.Min(overrideIndex, numericIndex)
            };
        }

        private int FindNextOverrideStart(string text, int startIndex)
        {
            int? result = null;

            foreach (var entry in _overrides.Value)
            {
                int foundIndex = text.IndexOf(entry.Text, startIndex, StringComparison.Ordinal);
                if (foundIndex < 0)
                {
                    continue;
                }

                if (result == null || foundIndex < result.Value)
                {
                    result = foundIndex;
                }
            }

            return result ?? -1;
        }

        private List<RubyOverrideEntry> LoadOverrides()
        {
            string filePath = Path.Combine(_env.ContentRootPath, "Data", "JapaneseRubyOverrides.json");
            if (!File.Exists(filePath))
            {
                return new List<RubyOverrideEntry>();
            }

            var entries = JsonSerializer.Deserialize<List<RubyOverrideEntry>>(File.ReadAllText(filePath))
                          ?? new List<RubyOverrideEntry>();

            return entries
                .Where(x => !string.IsNullOrWhiteSpace(x.Text) && !string.IsNullOrWhiteSpace(x.Ruby))
                .Select(x => new RubyOverrideEntry
                {
                    Text = x.Text,
                    Ruby = x.Ruby,
                    Reading = string.IsNullOrWhiteSpace(x.Reading) ? ExtractReadingFromRuby(x.Ruby) : x.Reading
                })
                .OrderByDescending(x => x.Text.Length)
                .ToList();
        }

        private static string ExtractReadingFromRuby(string rubyHtml)
        {
            if (string.IsNullOrWhiteSpace(rubyHtml))
            {
                return string.Empty;
            }

            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml($"<div id=\"override-root\">{rubyHtml}</div>");
            var root = document.GetElementbyId("override-root");
            if (root == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var node in root.ChildNodes)
            {
                if (node.Name.Equals("ruby", StringComparison.OrdinalIgnoreCase))
                {
                    var reading = node.SelectSingleNode("./rt")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(reading))
                    {
                        builder.Append(ToHiragana(HtmlEntity.DeEntitize(reading)));
                    }

                    continue;
                }

                builder.Append(ToHiragana(HtmlEntity.DeEntitize(node.InnerText)));
            }

            return builder.ToString();
        }

        private static void ApplyReadingHeuristics(List<JapaneseReadingToken> tokens)
        {
            for (int index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (!token.Surface.Equals("君", StringComparison.Ordinal) ||
                    !token.Reading.Equals("くん", StringComparison.Ordinal))
                {
                    continue;
                }

                string? nextSurface = GetNextMeaningfulSurface(tokens, index);
                if (nextSurface != null && IsPronounParticle(nextSurface))
                {
                    tokens[index] = token with { Reading = "きみ" };
                }
            }
        }

        private static string? GetNextMeaningfulSurface(IReadOnlyList<JapaneseReadingToken> tokens, int index)
        {
            for (int i = index + 1; i < tokens.Count; i++)
            {
                string surface = tokens[i].Surface;
                if (string.IsNullOrWhiteSpace(surface))
                {
                    continue;
                }

                if (surface is "（" or "(" or "「" or "『" or "[" or "【")
                {
                    continue;
                }

                return surface;
            }

            return null;
        }

        private static bool IsPronounParticle(string surface)
        {
            return surface is "に" or "を" or "が" or "は" or "も" or "の" or "と" or "へ" or "で" or "から" or "まで" or "って";
        }

        private MeCabIpaDicTagger CreateTagger()
        {
            string dicDir = Path.Combine(_env.WebRootPath, "ipadic");
            if (!Directory.Exists(dicDir))
            {
                throw new DirectoryNotFoundException($"找不到 IPA 字典目錄: {dicDir}");
            }

            return MeCabIpaDicTagger.Create(dicDir);
        }

        private static string WrapRuby(string surface, string reading)
        {
            return $"<ruby>{WebUtility.HtmlEncode(surface)}<rt>{WebUtility.HtmlEncode(reading)}</rt></ruby>";
        }

        private static string GetLeadingKana(string text)
        {
            int length = 0;
            while (length < text.Length && IsKana(text[length]))
            {
                length++;
            }

            return text[..length];
        }

        private static string GetTrailingKana(string text)
        {
            int length = 0;
            for (int i = text.Length - 1; i >= 0 && IsKana(text[i]); i--)
            {
                length++;
            }

            return length == 0 ? string.Empty : text[^length..];
        }

        private static IEnumerable<KanaRun> GetInternalKanaRuns(string text)
        {
            for (int i = 1; i < text.Length - 1; i++)
            {
                if (!IsKana(text[i]))
                {
                    continue;
                }

                int start = i;
                while (i < text.Length && IsKana(text[i]))
                {
                    i++;
                }

                int length = i - start;
                if (start > 0 && start + length < text.Length)
                {
                    yield return new KanaRun(start, length, text.Substring(start, length));
                }

                i--;
            }
        }

        private static bool IsAllKanji(string text)
        {
            foreach (var ch in text)
            {
                if (!IsKanji(ch))
                {
                    return false;
                }
            }

            return text.Length > 0;
        }

        private static bool ContainsKanji(string text)
        {
            foreach (var ch in text)
            {
                if (IsKanji(ch))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsJapanese(string text)
        {
            foreach (var ch in text)
            {
                if (IsKanji(ch) || IsKana(ch))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsKanji(char ch)
        {
            return (ch >= '\u4e00' && ch <= '\u9fff') || ch == '々' || ch == 'ヶ';
        }

        private static bool IsKana(char ch)
        {
            return (ch >= '\u3040' && ch <= '\u309f') ||
                   (ch >= '\u30a0' && ch <= '\u30ff') ||
                   ch == 'ー';
        }

        private static string ToHiragana(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);

            foreach (var ch in text)
            {
                if (ch >= '\u30a1' && ch <= '\u30f6')
                {
                    builder.Append((char)(ch - 0x60));
                }
                else
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private sealed record KanaRun(int Start, int Length, string Text);

        private sealed class RubyOverrideEntry
        {
            public string Text { get; set; } = string.Empty;
            public string Ruby { get; set; } = string.Empty;
            public string Reading { get; set; } = string.Empty;
        }

        public sealed record JapaneseReadingToken(string Surface, string Reading, string? RubyHtml = null);
    }
}
