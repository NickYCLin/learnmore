using System.Net;

namespace LearnMore.Services
{
    internal static class JapaneseNumericReading
    {
        private static readonly NumericCounter[] Counters =
        {
            new("時間", ReadHoursDuration),
            new("時以降", value => ReadHour(value) + "いこう"),
            new("時半", value => ReadHour(value) + "はん"),
            new("秒間", value => ReadDefault(value) + "びょうかん"),
            new("次元", value => ReadDefault(value) + "じげん"),
            new("ページ", value => ReadDefault(value) + "ページ"),
            new("センチ", value => ReadDefault(value) + "センチ"),
            new("パー", value => ReadDefault(value) + "パー"),
            new("キロ", value => ReadDefault(value) + "キロ"),
            new("マス", value => ReadDefault(value) + "マス"),
            new("月", ReadMonth),
            new("日", ReadDay),
            new("時", ReadHour),
            new("分", ReadMinute),
            new("秒", value => ReadDefault(value) + "びょう"),
            new("年", value => ReadDefault(value) + "ねん"),
            new("回", ReadKai),
            new("番", value => ReadDefault(value) + "ばん"),
            new("個", ReadKo),
            new("歳", value => ReadDefault(value) + "さい"),
            new("億", value => ReadDefault(value) + "おく"),
            new("万", value => ReadDefault(value) + "まん"),
            new("倍", value => ReadDefault(value) + "ばい"),
            new("度", value => ReadDefault(value) + "ど"),
            new("速", value => ReadDefault(value) + "そく"),
            new("本", ReadHon),
            new("割", value => ReadDefault(value) + "わり"),
            new("人", ReadPerson),
            new("つ", ReadTsu),
            new("ミリ", value => ReadDefault(value) + "ミリ")
        };

        public static NumericReadingMatch? TryMatch(string text, int startIndex)
        {
            if (startIndex >= text.Length || !IsDigit(text[startIndex]))
            {
                return null;
            }

            int digitEnd = startIndex;
            while (digitEnd < text.Length && IsDigit(text[digitEnd]))
            {
                digitEnd++;
            }

            string digits = NormalizeDigits(text[startIndex..digitEnd]);
            if (!int.TryParse(digits, out int value))
            {
                return null;
            }

            int suffixStart = digitEnd;
            while (suffixStart < text.Length && char.IsWhiteSpace(text[suffixStart]))
            {
                suffixStart++;
            }

            foreach (var counter in Counters)
            {
                if (!text.AsSpan(suffixStart).StartsWith(counter.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                int end = suffixStart + counter.Text.Length;
                string surface = text[startIndex..end];
                string reading = counter.Read(value);
                string ruby = $"<ruby>{WebUtility.HtmlEncode(surface)}<rt>{WebUtility.HtmlEncode(reading)}</rt></ruby>";
                return new NumericReadingMatch(surface, reading, ruby);
            }

            return null;
        }

        public static int FindNextStart(string text, int startIndex)
        {
            for (int index = startIndex; index < text.Length; index++)
            {
                if (TryMatch(text, index) != null)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string ReadDefault(int value)
        {
            if (value == 0)
            {
                return "れい";
            }

            if (value < 0 || value > 999999)
            {
                return value.ToString();
            }

            if (value >= 10000)
            {
                int tenThousands = value / 10000;
                int remainder = value % 10000;
                return ReadUnderTenThousand(tenThousands) + "まん" +
                       (remainder > 0 ? ReadUnderTenThousand(remainder) : string.Empty);
            }

            return ReadUnderTenThousand(value);
        }

        private static string ReadUnderTenThousand(int value)
        {
            var parts = new List<string>();

            int thousands = value / 1000;
            if (thousands > 0)
            {
                parts.Add(thousands switch
                {
                    1 => "せん",
                    3 => "さんぜん",
                    8 => "はっせん",
                    _ => BasicDigit(thousands) + "せん"
                });
            }

            int hundreds = value % 1000 / 100;
            if (hundreds > 0)
            {
                parts.Add(hundreds switch
                {
                    1 => "ひゃく",
                    3 => "さんびゃく",
                    6 => "ろっぴゃく",
                    8 => "はっぴゃく",
                    _ => BasicDigit(hundreds) + "ひゃく"
                });
            }

            int tens = value % 100 / 10;
            if (tens > 0)
            {
                parts.Add(tens == 1 ? "じゅう" : BasicDigit(tens) + "じゅう");
            }

            int ones = value % 10;
            if (ones > 0)
            {
                parts.Add(BasicDigit(ones));
            }

            return string.Concat(parts);
        }

        private static string ReadPerson(int value)
        {
            return value switch
            {
                1 => "ひとり",
                2 => "ふたり",
                4 => "よにん",
                _ => ReadDefault(value) + "にん"
            };
        }

        private static string ReadMonth(int value)
        {
            return value switch
            {
                1 => "いちがつ",
                2 => "にがつ",
                3 => "さんがつ",
                4 => "しがつ",
                5 => "ごがつ",
                6 => "ろくがつ",
                7 => "しちがつ",
                8 => "はちがつ",
                9 => "くがつ",
                10 => "じゅうがつ",
                11 => "じゅういちがつ",
                12 => "じゅうにがつ",
                _ => ReadDefault(value) + "がつ"
            };
        }

        private static string ReadDay(int value)
        {
            return value switch
            {
                1 => "いちにち",
                2 => "ふつか",
                3 => "みっか",
                4 => "よっか",
                5 => "いつか",
                6 => "むいか",
                7 => "なのか",
                8 => "ようか",
                9 => "ここのか",
                10 => "とおか",
                14 => "じゅうよっか",
                20 => "はつか",
                24 => "にじゅうよっか",
                _ => ReadDefault(value) + "にち"
            };
        }

        private static string ReadHour(int value)
        {
            return value switch
            {
                0 => "れいじ",
                4 => "よじ",
                7 => "しちじ",
                9 => "くじ",
                _ => ReadDefault(value) + "じ"
            };
        }

        private static string ReadHoursDuration(int value)
        {
            return value switch
            {
                4 => "よじかん",
                7 => "ななじかん",
                9 => "きゅうじかん",
                _ => ReadDefault(value) + "じかん"
            };
        }

        private static string ReadMinute(int value)
        {
            return value switch
            {
                1 => "いっぷん",
                3 => "さんぷん",
                4 => "よんぷん",
                6 => "ろっぷん",
                8 => "はっぷん",
                10 => "じゅっぷん",
                20 => "にじゅっぷん",
                30 => "さんじゅっぷん",
                40 => "よんじゅっぷん",
                50 => "ごじゅっぷん",
                _ => ReadDefault(value) + "ふん"
            };
        }

        private static string ReadKai(int value)
        {
            return value switch
            {
                1 => "いっかい",
                6 => "ろっかい",
                8 => "はっかい",
                10 => "じゅっかい",
                _ => ReadDefault(value) + "かい"
            };
        }

        private static string ReadKo(int value)
        {
            return value switch
            {
                1 => "いっこ",
                6 => "ろっこ",
                8 => "はっこ",
                10 => "じゅっこ",
                100 => "ひゃっこ",
                _ => ReadDefault(value) + "こ"
            };
        }

        private static string ReadHon(int value)
        {
            return value switch
            {
                1 => "いっぽん",
                3 => "さんぼん",
                6 => "ろっぽん",
                8 => "はっぽん",
                10 => "じゅっぽん",
                _ => ReadDefault(value) + "ほん"
            };
        }

        private static string ReadTsu(int value)
        {
            return value switch
            {
                1 => "ひとつ",
                2 => "ふたつ",
                3 => "みっつ",
                4 => "よっつ",
                5 => "いつつ",
                6 => "むっつ",
                7 => "ななつ",
                8 => "やっつ",
                9 => "ここのつ",
                _ => ReadDefault(value) + "つ"
            };
        }

        private static string BasicDigit(int value)
        {
            return value switch
            {
                1 => "いち",
                2 => "に",
                3 => "さん",
                4 => "よん",
                5 => "ご",
                6 => "ろく",
                7 => "なな",
                8 => "はち",
                9 => "きゅう",
                _ => string.Empty
            };
        }

        private static bool IsDigit(char ch)
        {
            return ch is >= '0' and <= '9' or >= '０' and <= '９';
        }

        private static string NormalizeDigits(string text)
        {
            var builder = new char[text.Length];
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                builder[index] = ch is >= '０' and <= '９'
                    ? (char)('0' + ch - '０')
                    : ch;
            }

            return new string(builder);
        }

        private sealed record NumericCounter(string Text, Func<int, string> Read);

        public sealed record NumericReadingMatch(string Surface, string Reading, string RubyHtml);
    }
}
