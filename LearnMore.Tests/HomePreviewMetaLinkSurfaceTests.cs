using Xunit;

namespace LearnMore.Tests;

public class HomePreviewMetaLinkSurfaceTests
{
    [Fact]
    public void HomePreview_ShouldLinkSongMetaAreaToLyricsPage()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "wwwroot",
            "js",
            "home.js"));

        Assert.Contains("document.createElement('a')", source);
        Assert.Contains("preview-meta preview-meta-link", source);
        Assert.Contains("meta.href = lyricsHref || '#'", source);
        Assert.Contains("前往歌曲頁：", source);
    }

    [Fact]
    public void HomeCss_ShouldKeepPreviewMetaLinkLookingLikeTheOriginalPanel()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "wwwroot",
            "css",
            "home.css"));

        Assert.Contains(".preview-meta-link", source);
        Assert.Contains("text-decoration: none", source);
        Assert.Contains("cursor: pointer", source);
    }
}
