using Xunit;

namespace LearnMore.Tests;

public class YouTubeIframeConsoleWarningSurfaceTests
{
    [Fact]
    public void LyricsView_ShouldStripUnsupportedWebShareFeatureFromYouTubeIframeAllow()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Lyrics",
            "Index.cshtml"));

        Assert.Contains("installYouTubeIframeAllowSanitizer", source);
        Assert.Contains("part !== 'web-share'", source);
        Assert.Contains("HTMLIFrameElement.prototype.setAttribute", source);
        Assert.True(source.IndexOf("installYouTubeIframeAllowSanitizer();", StringComparison.Ordinal) <
                    source.IndexOf("https://www.youtube.com/iframe_api", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupPlayerView_ShouldInstallYouTubeAllowSanitizerBeforeLoadingIframeApi()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "GroupPlayer",
            "Play.cshtml"));

        Assert.Contains("installYouTubeIframeAllowSanitizer", source);
        Assert.Contains("part !== 'web-share'", source);
        Assert.Contains("HTMLIFrameElement.prototype.setAttribute", source);
        Assert.True(source.IndexOf("installYouTubeIframeAllowSanitizer();", StringComparison.Ordinal) <
                    source.IndexOf("https://www.youtube.com/iframe_api", StringComparison.Ordinal));
    }
}
