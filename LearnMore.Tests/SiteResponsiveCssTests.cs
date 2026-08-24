using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LearnMore.Tests;

public class SiteResponsiveCssTests
{
    [Fact]
    public void SiteCss_ShouldKeepSharedPagesResponsiveOnSmallScreens()
    {
        var css = File.ReadAllText(ResolveProjectPath("LearnMore/wwwroot/css/site.css"));

        Assert.Contains("overflow-x: clip", css);
        Assert.Contains("@media (max-width: 640px)", css);
        Assert.Contains("font-size: 16px", css);
        Assert.Contains("min-height: 44px", css);
        Assert.Contains("max-width: 100%", css);
    }

    [Fact]
    public void LyricsCss_ShouldNotResizeHighlightedSyncedLyric()
    {
        var css = File.ReadAllText(ResolveProjectPath("LearnMore/wwwroot/css/lyrics.css"));

        var highlightRule = ExtractCssRule(css, ".lyrics-line.highlight");
        Assert.DoesNotContain("font-size", highlightRule);
        Assert.DoesNotContain("font-weight", highlightRule);
        Assert.DoesNotContain("padding", highlightRule);
        Assert.DoesNotContain("border-left", highlightRule);
        Assert.DoesNotContain("transform", highlightRule);
        Assert.Contains("inset 4px 0 0", highlightRule);

        var timestampEditorHighlightRule = ExtractCssRule(css, ".lyrics-line.highlight:has(.timestamp-editor[style*=\"flex\"])");
        Assert.DoesNotContain("scale(", timestampEditorHighlightRule);
        Assert.Contains("transform: none", timestampEditorHighlightRule);
    }

    private static string ExtractCssRule(string css, string selector)
    {
        var escapedSelector = Regex.Escape(selector);
        var match = Regex.Match(css, escapedSelector + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"Could not find CSS rule for selector {selector}.");
        return match.Groups["body"].Value;
    }

    private static string ResolveProjectPath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not resolve project file: {relativePath}");
    }
}
