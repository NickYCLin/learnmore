using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class ShareMetadataBuilderTests
{
    [Fact]
    public void Build_ShouldUseDefaultsAndNormalizeRelativeImageUrl()
    {
        var metadata = ShareMetadataBuilder.Build(
            siteRoot: "https://magicplus-design.serveirc.com",
            currentPathAndQuery: "/LearnMore",
            pageTitle: "首頁",
            description: null,
            shareImageUrl: "/LearnMore/proxy/catframe/1");

        Assert.Equal("首頁 - ビビ學日語｜日文歌詞同步學習平台", metadata.PageTitle);
        Assert.Equal("首頁 - ビビ學日語｜日文歌詞同步學習", metadata.OpenGraphTitle);
        Assert.Equal("免費日語歌詞學習平台！提供 YouTube 同步播放、假名標註（振り仮名）、羅馬拼音、繁體中文翻譯。用唱歌學日文，邊聽邊學，輕鬆記住日文歌詞，適合日語初學者到進階學習者。", metadata.Description);
        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore", metadata.CanonicalUrl);
        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore/proxy/catframe/1", metadata.OpenGraphImageUrl);
        Assert.Equal("https://magicplus-design.serveirc.com/apple-touch-icon.png?v=20260421", metadata.AppleTouchIconUrl);
        Assert.Equal("ビビ學日語", metadata.AppleMobileWebAppTitle);
        Assert.Equal("yes", metadata.AppleMobileWebAppCapable);
        Assert.Equal("#6366f1", metadata.ThemeColor);
        Assert.Equal("summary_large_image", metadata.TwitterCard);
        Assert.Equal("zh-Hant", metadata.HtmlLanguage);
    }

    [Fact]
    public void Build_ShouldHonorPathBaseWithoutDuplicatingImagePath()
    {
        var metadata = ShareMetadataBuilder.Build(
            siteRoot: "https://magicplus-design.serveirc.com/LearnMore",
            currentPathAndQuery: "/",
            pageTitle: "首頁",
            description: null,
            shareImageUrl: "/LearnMore/proxy/catframe/1");

        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore/", metadata.CanonicalUrl);
        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore/proxy/catframe/1", metadata.OpenGraphImageUrl);
        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore/apple-touch-icon.png?v=20260421", metadata.AppleTouchIconUrl);
    }

    [Fact]
    public void Build_ShouldPreserveCustomDescriptionAndAbsoluteImageUrl()
    {
        var metadata = ShareMetadataBuilder.Build(
            siteRoot: "https://magicplus-design.serveirc.com",
            currentPathAndQuery: "/LearnMore/Lyrics/abc123",
            pageTitle: "殘酷天使的行動綱領",
            description: "逐句同步歌詞、假名與中文翻譯。",
            shareImageUrl: "https://cdn.example.com/share.png");

        Assert.Equal("殘酷天使的行動綱領 - ビビ學日語｜日文歌詞同步學習平台", metadata.PageTitle);
        Assert.Equal("殘酷天使的行動綱領 - ビビ學日語｜日文歌詞同步學習", metadata.OpenGraphTitle);
        Assert.Equal("逐句同步歌詞、假名與中文翻譯。", metadata.Description);
        Assert.Equal("https://magicplus-design.serveirc.com/LearnMore/Lyrics/abc123", metadata.CanonicalUrl);
        Assert.Equal("https://cdn.example.com/share.png", metadata.OpenGraphImageUrl);
    }

    [Fact]
    public void Build_ShouldUseSearchTitleOverrideForHtmlTitleOnly()
    {
        var metadata = ShareMetadataBuilder.Build(
            siteRoot: "https://magicplus-design.serveirc.com",
            currentPathAndQuery: "/LearnMore/Lyrics/abc123",
            pageTitle: "♡Emotion",
            description: null,
            shareImageUrl: null,
            searchTitle: "♡Emotion 日文歌詞・中文翻譯・羅馬拼音｜ビビ學日語");

        Assert.Equal("♡Emotion 日文歌詞・中文翻譯・羅馬拼音｜ビビ學日語", metadata.PageTitle);
        Assert.Equal("♡Emotion - ビビ學日語｜日文歌詞同步學習", metadata.OpenGraphTitle);
    }

    [Fact]
    public void Build_ShouldUseOpenGraphTitleOverride()
    {
        var metadata = ShareMetadataBuilder.Build(
            siteRoot: "https://magicplus-design.serveirc.com",
            currentPathAndQuery: "/LearnMore/",
            pageTitle: "首頁",
            description: null,
            shareImageUrl: null,
            searchTitle: "ビビ學日語｜日文歌詞・中文翻譯・羅馬拼音同步學習",
            openGraphTitle: "ビビ學日語｜日文歌詞・中文翻譯・羅馬拼音同步學習");

        Assert.Equal("ビビ學日語｜日文歌詞・中文翻譯・羅馬拼音同步學習", metadata.PageTitle);
        Assert.Equal("ビビ學日語｜日文歌詞・中文翻譯・羅馬拼音同步學習", metadata.OpenGraphTitle);
    }
}
