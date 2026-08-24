using System.Text;
using System.Text.RegularExpressions;

namespace LearnMore.Services
{
    public static class JapaneseRomanSanitizer
    {
        private static readonly HashSet<string> SeparatorTokens = new(StringComparer.Ordinal)
        {
            "は", "が", "を", "に", "へ", "と", "の", "も", "や", "か", "な", "ね", "よ", "わ", "ぞ", "ぜ", "さ",
            "から", "まで", "より", "だけ", "ほど", "くらい", "ぐらい", "など", "ので", "のに", "やら", "とか"
        };

        private static readonly HashSet<string> StandaloneKanaSuffixTokens = new(StringComparer.Ordinal)
        {
            "さん", "ちゃん", "くん", "さま", "様", "殿", "氏", "しまう", "いる", "いく", "ほしい", "わけ", "しか", "でも", "いい", "なら", "らしい", "みたい", "やすい", "にくい", "そう", "です"
        };

        private static readonly HashSet<string> GariCompoundSuffixTokens = new(StringComparer.Ordinal)
        {
            "屋", "症", "性", "や"
        };

        private static readonly HashSet<string> AuxiliaryStartTokens = new(StringComparer.Ordinal)
        {
            "い", "いく", "いる", "おく", "しまう", "しまい", "みる", "く", "くる", "くれる", "ほしい"
        };

        private static readonly string[] AuxiliaryStartPrefixes =
        {
            "い", "くれ", "しま", "ほし"
        };

        private static readonly string[] CompoundStandaloneSuffixes =
        {
            "ほど", "くらい", "ぐらい"
        };

        private static readonly Dictionary<string, string> DigraphMap = new(StringComparer.Ordinal)
        {
            ["きゃ"] = "kya",
            ["きゅ"] = "kyu",
            ["きょ"] = "kyo",
            ["ぎゃ"] = "gya",
            ["ぎゅ"] = "gyu",
            ["ぎょ"] = "gyo",
            ["しゃ"] = "sha",
            ["しゅ"] = "shu",
            ["しょ"] = "sho",
            ["じゃ"] = "ja",
            ["じゅ"] = "ju",
            ["じょ"] = "jo",
            ["ちゃ"] = "cha",
            ["ちゅ"] = "chu",
            ["ちょ"] = "cho",
            ["にゃ"] = "nya",
            ["にゅ"] = "nyu",
            ["にょ"] = "nyo",
            ["ひゃ"] = "hya",
            ["ひゅ"] = "hyu",
            ["ひょ"] = "hyo",
            ["びゃ"] = "bya",
            ["びゅ"] = "byu",
            ["びょ"] = "byo",
            ["ぴゃ"] = "pya",
            ["ぴゅ"] = "pyu",
            ["ぴょ"] = "pyo",
            ["みゃ"] = "mya",
            ["みゅ"] = "myu",
            ["みょ"] = "myo",
            ["りゃ"] = "rya",
            ["りゅ"] = "ryu",
            ["りょ"] = "ryo"
        };

        private static readonly Dictionary<char, string> KanaMap = new()
        {
            ['あ'] = "a",
            ['い'] = "i",
            ['う'] = "u",
            ['え'] = "e",
            ['お'] = "o",
            ['か'] = "ka",
            ['き'] = "ki",
            ['く'] = "ku",
            ['け'] = "ke",
            ['こ'] = "ko",
            ['が'] = "ga",
            ['ぎ'] = "gi",
            ['ぐ'] = "gu",
            ['げ'] = "ge",
            ['ご'] = "go",
            ['さ'] = "sa",
            ['し'] = "shi",
            ['す'] = "su",
            ['せ'] = "se",
            ['そ'] = "so",
            ['ざ'] = "za",
            ['じ'] = "ji",
            ['ず'] = "zu",
            ['ぜ'] = "ze",
            ['ぞ'] = "zo",
            ['た'] = "ta",
            ['ち'] = "chi",
            ['つ'] = "tsu",
            ['て'] = "te",
            ['と'] = "to",
            ['だ'] = "da",
            ['ぢ'] = "ji",
            ['づ'] = "zu",
            ['で'] = "de",
            ['ど'] = "do",
            ['な'] = "na",
            ['に'] = "ni",
            ['ぬ'] = "nu",
            ['ね'] = "ne",
            ['の'] = "no",
            ['は'] = "ha",
            ['ひ'] = "hi",
            ['ふ'] = "fu",
            ['へ'] = "he",
            ['ほ'] = "ho",
            ['ば'] = "ba",
            ['び'] = "bi",
            ['ぶ'] = "bu",
            ['べ'] = "be",
            ['ぼ'] = "bo",
            ['ぱ'] = "pa",
            ['ぴ'] = "pi",
            ['ぷ'] = "pu",
            ['ぺ'] = "pe",
            ['ぽ'] = "po",
            ['ま'] = "ma",
            ['み'] = "mi",
            ['む'] = "mu",
            ['め'] = "me",
            ['も'] = "mo",
            ['や'] = "ya",
            ['ゆ'] = "yu",
            ['よ'] = "yo",
            ['ら'] = "ra",
            ['り'] = "ri",
            ['る'] = "ru",
            ['れ'] = "re",
            ['ろ'] = "ro",
            ['わ'] = "wa",
            ['を'] = "o",
            ['ん'] = "n",
            ['ぁ'] = "a",
            ['ぃ'] = "i",
            ['ぅ'] = "u",
            ['ぇ'] = "e",
            ['ぉ'] = "o",
            ['ゔ'] = "vu"
        };

        private static readonly Dictionary<string, string> LongVowelMap = new(StringComparer.Ordinal)
        {
            ["aa"] = "ā",
            ["uu"] = "ū",
            ["ee"] = "ē",
            ["oo"] = "ō",
            ["ou"] = "ō"
        };

        public static string NormalizeWithContext(
            string? japaneseText,
            string? romanText,
            JapaneseRubyGeneratorService rubyGenerator)
        {
            string normalized = Normalize(romanText);
            if (string.IsNullOrWhiteSpace(japaneseText))
            {
                return normalized;
            }

            var tokens = rubyGenerator.Tokenize(NormalizeJapaneseTextForTokenization(japaneseText)).ToList();
            if (tokens.Count == 0)
            {
                return normalized;
            }

            int kimiCount = tokens
                .Count(token => token.Surface.Equals("君", StringComparison.Ordinal) &&
                                token.Reading.Equals("きみ", StringComparison.Ordinal));

            if (kimiCount > 0)
            {
                normalized = ReplaceWholeWord(normalized, "kun", "kimi", kimiCount);
            }

            var romanChunks = SplitRomanChunks(normalized);
            var fallbackChunks = RomanizeTokenReadings(tokens);
            if (fallbackChunks.Count != tokens.Count)
            {
                return normalized;
            }

            if (romanChunks.Count != tokens.Count)
            {
                romanChunks = fallbackChunks;
            }
            else if (ShouldPreferFallbackForNumericReadings(tokens, romanChunks, fallbackChunks))
            {
                romanChunks = fallbackChunks;
            }
            else
            {
                romanChunks = romanChunks
                    .Select((chunk, index) => NormalizeChunkAgainstReading(tokens[index], chunk, fallbackChunks[index]))
                    .ToList();
            }

            return Normalize(MergeRomanChunks(tokens, romanChunks));
        }

        private static bool ShouldPreferFallbackForNumericReadings(
            IReadOnlyList<JapaneseRubyGeneratorService.JapaneseReadingToken> tokens,
            IReadOnlyList<string> romanChunks,
            IReadOnlyList<string> fallbackChunks)
        {
            for (int index = 0; index < tokens.Count; index++)
            {
                if (!ContainsAsciiOrFullWidthDigit(tokens[index].Surface))
                {
                    continue;
                }

                if (!string.Equals(romanChunks[index], fallbackChunks[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace('\u3000', ' ');
            normalized = normalized
                .Replace('（', '(')
                .Replace('）', ')')
                .Replace('，', ',')
                .Replace('。', '.')
                .Replace('：', ':')
                .Replace('；', ';')
                .Replace('！', '!')
                .Replace('？', '?');

            normalized = Regex.Replace(normalized, @"\s+([,.;:!?])", "$1");
            normalized = Regex.Replace(normalized, @"([,.;:!?])(?=\S)", "$1 ");
            normalized = Regex.Replace(normalized, @"\(\s+", "(");
            normalized = Regex.Replace(normalized, @"\s+\)", ")");
            normalized = Regex.Replace(normalized, @"\)(?=[A-Za-zāīūēō])", ") ");
            normalized = Regex.Replace(normalized, @"\s{2,}", " ");

            return normalized.Trim();
        }

        private static List<string> SplitRomanChunks(string text)
        {
            return Regex.Matches(text, @"\S+")
                .Select(match => match.Value)
                .ToList();
        }

        private static List<string> RomanizeTokenReadings(IReadOnlyList<JapaneseRubyGeneratorService.JapaneseReadingToken> tokens)
        {
            var results = new List<string>(tokens.Count);
            for (int index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                string roman = RomanizeReading(token.Reading, token.Surface);

                if (!string.IsNullOrWhiteSpace(token.Reading)
                    && token.Reading.EndsWith('っ')
                    && !IsKatakanaLikeInterjection(token.Surface)
                    && index + 1 < tokens.Count)
                {
                    string nextRoman = RomanizeReading(tokens[index + 1].Reading, tokens[index + 1].Surface);
                    if (!string.IsNullOrEmpty(nextRoman))
                    {
                        roman += nextRoman[0];
                    }
                }

                results.Add(roman);
            }

            return results;
        }

        private static string NormalizeJapaneseTextForTokenization(string text)
        {
            return text.Normalize(NormalizationForm.FormKC);
        }

        private static string NormalizeJapaneseTextForReading(string text)
        {
            string normalized = NormalizeJapaneseTextForTokenization(text);
            var chars = normalized.Select(ch => ch is >= 'ァ' and <= 'ヶ'
                ? (char)(ch - 'ァ' + 'ぁ')
                : ch);
            return new string(chars.ToArray());
        }

        private static string RomanizeReading(string? reading, string surface)
        {
            if (string.IsNullOrWhiteSpace(reading))
            {
                return surface;
            }

            var text = NormalizeJapaneseTextForReading(reading).Trim();
            var builder = new List<string>();
            int index = 0;

            while (index < text.Length)
            {
                char current = text[index];

                if (current == 'っ')
                {
                    string nextRoman = PeekRoman(text, index + 1);
                    if (!string.IsNullOrEmpty(nextRoman))
                    {
                        builder.Add(nextRoman[0].ToString());
                    }
                    index++;
                    continue;
                }

                if (current == 'ー')
                {
                    if (builder.Count > 0)
                    {
                        builder[^1] = ProlongLastVowel(builder[^1]);
                    }
                    index++;
                    continue;
                }

                if (index + 1 < text.Length)
                {
                    string pair = text.Substring(index, 2);
                    if (DigraphMap.TryGetValue(pair, out var digraphRoman))
                    {
                        builder.Add(digraphRoman);
                        index += 2;
                        continue;
                    }
                }

                if (KanaMap.TryGetValue(current, out var roman))
                {
                    builder.Add(roman);
                }
                else
                {
                    builder.Add(current.ToString());
                }

                index++;
            }

            return ApplyLongVowels(string.Concat(builder));
        }

        private static string PeekRoman(string text, int index)
        {
            if (index >= text.Length)
            {
                return string.Empty;
            }

            if (index + 1 < text.Length)
            {
                string pair = text.Substring(index, 2);
                if (DigraphMap.TryGetValue(pair, out var digraphRoman))
                {
                    return digraphRoman;
                }
            }

            return KanaMap.TryGetValue(text[index], out var roman) ? roman : string.Empty;
        }

        private static string ProlongLastVowel(string roman)
        {
            if (string.IsNullOrEmpty(roman))
            {
                return roman;
            }

            return roman[^1] switch
            {
                'a' => roman[..^1] + 'ā',
                'i' => roman[..^1] + 'ī',
                'u' => roman[..^1] + 'ū',
                'e' => roman[..^1] + 'ē',
                'o' => roman[..^1] + 'ō',
                _ => roman
            };
        }

        private static string ApplyLongVowels(string roman)
        {
            foreach (var pair in LongVowelMap)
            {
                roman = roman.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            }

            return roman;
        }

        private static string MergeRomanChunks(
            IReadOnlyList<JapaneseRubyGeneratorService.JapaneseReadingToken> tokens,
            IReadOnlyList<string> romanChunks)
        {
            var merged = new List<string>();
            string current = NormalizeStandaloneCompoundChunk(tokens[0].Surface, romanChunks[0]);

            for (int index = 1; index < tokens.Count; index++)
            {
                string normalizedChunk = NormalizeStandaloneCompoundChunk(tokens[index].Surface, romanChunks[index]);

                if (IsPersonCountSurface(tokens[index - 1].Surface))
                {
                    normalizedChunk = SplitLeadingParticleDe(tokens[index].Surface, normalizedChunk);
                }

                if (IsSplitTagariyaSequence(tokens, index))
                {
                    current += normalizedChunk;
                    current += NormalizeStandaloneCompoundChunk(tokens[index + 1].Surface, romanChunks[index + 1]);
                    index++;
                    continue;
                }

                if (index >= 2 &&
                    tokens[index - 1].Surface == "で" &&
                    IsPersonCountSurface(tokens[index - 2].Surface))
                {
                    merged.Add(current);
                    current = normalizedChunk;
                    continue;
                }

                if (ShouldMergeWithPrevious(tokens[index - 1].Surface, tokens[index].Surface))
                {
                    current += normalizedChunk;
                    continue;
                }

                merged.Add(current);
                current = normalizedChunk;
            }

            merged.Add(current);
            return string.Join(" ", merged);
        }

        private static string NormalizeStandaloneCompoundChunk(string surface, string romanChunk)
        {
            if (!IsStandaloneCompoundSurface(surface))
            {
                return romanChunk;
            }

            string? matchedSuffix = CompoundStandaloneSuffixes
                .FirstOrDefault(suffix => surface.EndsWith(suffix, StringComparison.Ordinal));
            if (matchedSuffix is null)
            {
                return romanChunk;
            }

            string suffixRoman = RomanizeReading(matchedSuffix, matchedSuffix);
            if (!romanChunk.EndsWith(suffixRoman, StringComparison.Ordinal) ||
                romanChunk.Length <= suffixRoman.Length)
            {
                return romanChunk;
            }

            string prefixRoman = romanChunk[..^suffixRoman.Length];
            return $"{prefixRoman} {suffixRoman}";
        }

        private static bool ShouldMergeWithPrevious(string previousSurface, string currentSurface)
        {
            if (string.IsNullOrWhiteSpace(previousSurface) || string.IsNullOrWhiteSpace(currentSurface))
            {
                return false;
            }

            if (previousSurface.EndsWith("がり", StringComparison.Ordinal) &&
                GariCompoundSuffixTokens.Contains(currentSurface))
            {
                return true;
            }

            if (previousSurface.EndsWith("っぽ", StringComparison.Ordinal) && currentSurface == "さ")
            {
                return true;
            }

            if (previousSurface.EndsWith("らし", StringComparison.Ordinal) && currentSurface == "さ")
            {
                return true;
            }

            if (previousSurface == "がち" && currentSurface is "だ" or "で")
            {
                return false;
            }

            if (previousSurface == "気味" && currentSurface is "だ" or "で")
            {
                return false;
            }

            if (previousSurface == "すぎ" && currentSurface is "だ" or "で" or "て")
            {
                return false;
            }

            if (previousSurface.EndsWith("っぽ", StringComparison.Ordinal) && currentSurface == "げ")
            {
                return false;
            }

            if (currentSurface == "ない" &&
                (previousSurface.EndsWith("気", StringComparison.Ordinal) ||
                 previousSurface.EndsWith("げ", StringComparison.Ordinal)))
            {
                return false;
            }

            if (currentSurface is "だ" or "で" &&
                (previousSurface.EndsWith("気", StringComparison.Ordinal) ||
                 previousSurface.EndsWith("げ", StringComparison.Ordinal)))
            {
                return false;
            }

            if (previousSurface == "げ" && currentSurface == "で")
            {
                return false;
            }

            if (previousSurface == "さ" && currentSurface == "ん")
            {
                return true;
            }

            if (IsPersonCountSurface(previousSurface) && currentSurface.StartsWith("で", StringComparison.Ordinal))
            {
                return false;
            }

            if (IsKatakanaLikeInterjection(previousSurface))
            {
                return false;
            }

            if (!IsAllHiragana(currentSurface))
            {
                return false;
            }

            if (currentSurface == "そう" && previousSurface == "しまい")
            {
                return true;
            }

            if (SeparatorTokens.Contains(currentSurface) || StandaloneKanaSuffixTokens.Contains(currentSurface))
            {
                return false;
            }

            if (currentSurface == "じゃ")
            {
                return false;
            }

            if (currentSurface == "でし")
            {
                return false;
            }

            if (SeparatorTokens.Contains(previousSurface) ||
                StandaloneKanaSuffixTokens.Contains(previousSurface) ||
                IsStandaloneCompoundSurface(previousSurface))
            {
                return false;
            }

            if (previousSurface is "て" or "で" && IsAuxiliaryStartToken(currentSurface))
            {
                return false;
            }

            return true;
        }

        private static bool IsStandaloneCompoundSurface(string surface)
        {
            if (string.IsNullOrWhiteSpace(surface) || !IsAllHiragana(surface))
            {
                return false;
            }

            return CompoundStandaloneSuffixes.Any(suffix =>
                surface.Length > suffix.Length &&
                surface.EndsWith(suffix, StringComparison.Ordinal));
        }

        private static bool IsSplitTagariyaSequence(
            IReadOnlyList<JapaneseRubyGeneratorService.JapaneseReadingToken> tokens,
            int index)
        {
            return index + 1 < tokens.Count &&
                   index > 0 &&
                   tokens[index - 1].Surface == "た" &&
                   tokens[index].Surface == "が" &&
                   tokens[index + 1].Surface == "りや";
        }

        private static bool IsPersonCountSurface(string surface)
        {
            return surface is "一人" or "二人" or "1人" or "2人" or "１人" or "２人";
        }

        private static string SplitLeadingParticleDe(string surface, string romanChunk)
        {
            if (surface.Length <= 1 ||
                !surface.StartsWith("で", StringComparison.Ordinal) ||
                !romanChunk.StartsWith("de", StringComparison.Ordinal) ||
                romanChunk.Length <= 2 ||
                char.IsWhiteSpace(romanChunk[2]))
            {
                return romanChunk;
            }

            return $"de {romanChunk[2..]}";
        }

        private static string NormalizeChunkAgainstReading(
            JapaneseRubyGeneratorService.JapaneseReadingToken token,
            string romanChunk,
            string fallbackChunk)
        {
            if (string.IsNullOrWhiteSpace(romanChunk) || string.IsNullOrWhiteSpace(token.Reading))
            {
                return string.IsNullOrWhiteSpace(romanChunk) ? fallbackChunk : romanChunk;
            }

            if (token.Reading.EndsWith('っ') &&
                romanChunk.EndsWith("tsu", StringComparison.Ordinal) &&
                !fallbackChunk.EndsWith("tsu", StringComparison.Ordinal))
            {
                return fallbackChunk;
            }

            return romanChunk;
        }

        private static bool IsKatakanaLikeInterjection(string surface)
        {
            if (string.IsNullOrWhiteSpace(surface))
            {
                return false;
            }

            string normalized = surface.Normalize(NormalizationForm.FormKC);
            return normalized.Any(ch => ch is >= 'ァ' and <= 'ヶ')
                && normalized.All(ch => ch is >= 'ァ' and <= 'ヶ' or 'ー' or 'ッ');
        }

        private static bool IsAuxiliaryStartToken(string surface)
        {
            if (AuxiliaryStartTokens.Contains(surface))
            {
                return true;
            }

            return AuxiliaryStartPrefixes.Any(prefix =>
                surface.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool IsAllHiragana(string text)
        {
            return text.All(ch => ch is >= 'ぁ' and <= 'ゖ' or 'ー');
        }

        private static bool ContainsAsciiOrFullWidthDigit(string text)
        {
            return text.Any(ch => ch is >= '0' and <= '9' or >= '０' and <= '９');
        }

        private static string ReplaceWholeWord(string text, string source, string target, int maxReplacements)
        {
            if (maxReplacements <= 0)
            {
                return text;
            }

            int replacements = 0;
            return Regex.Replace(text, $@"\b{Regex.Escape(source)}\b", match =>
            {
                if (replacements >= maxReplacements)
                {
                    return match.Value;
                }

                replacements++;
                return target;
            });
        }
    }
}
