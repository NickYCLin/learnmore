using System.Text;
using HtmlAgilityPack;

namespace LearnMore.Services
{
    public static class JapaneseRubySanitizer
    {
        public static string NormalizeRubyHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var document = new HtmlDocument();
            document.LoadHtml($"<div id=\"ruby-root\">{html}</div>");

            var root = document.GetElementbyId("ruby-root");
            if (root == null)
            {
                return html;
            }

            foreach (var rpNode in root.SelectNodes(".//rp") ?? Enumerable.Empty<HtmlNode>())
            {
                rpNode.Remove();
            }

            foreach (var rubyNode in root.SelectNodes(".//ruby")?.ToList() ?? new List<HtmlNode>())
            {
                var readingNode = rubyNode.SelectSingleNode("./rt");
                if (readingNode == null)
                {
                    continue;
                }

                var baseText = GetRubyBaseText(rubyNode);
                var readingText = readingNode.InnerText?.Trim() ?? string.Empty;

                if (ShouldFlattenRuby(baseText, readingText))
                {
                    rubyNode.ParentNode?.ReplaceChild(document.CreateTextNode(baseText), rubyNode);
                }
            }

            return root.InnerHtml.Trim();
        }

        private static string GetRubyBaseText(HtmlNode rubyNode)
        {
            var builder = new StringBuilder();

            foreach (var child in rubyNode.ChildNodes)
            {
                if (child.Name.Equals("rt", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.Equals("rp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                builder.Append(child.InnerText);
            }

            return HtmlEntity.DeEntitize(builder.ToString());
        }

        private static bool ShouldFlattenRuby(string baseText, string readingText)
        {
            if (string.IsNullOrWhiteSpace(baseText))
            {
                return true;
            }

            if (!ContainsKanji(baseText))
            {
                return true;
            }

            return NormalizeKana(baseText) == NormalizeKana(readingText);
        }

        private static bool ContainsKanji(string text)
        {
            foreach (var ch in text)
            {
                if (ch >= '\u4e00' && ch <= '\u9fff')
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeKana(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);

            foreach (var ch in HtmlEntity.DeEntitize(text))
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

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
    }
}
