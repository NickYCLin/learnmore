using LearnMore.Models;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public class MarumaruCrawlerServiceTests
{
    [Fact]
    public void ExtractLyricsFromHtml_ParsesCurrentTranslateZhClassShape()
    {
        const string html = """
<select id="input-repeat-start">
  <option value="1">(1)はじまりはもう思い出せない</option>
  <option value="2">(2)それは とてもドラマチックで</option>
</select>
<p lang="zh-Hant" class="lyrics-translate-zh font-zh2 mt-1 translate-zh" style="">開始已經回想不起來了</p>
<p lang="en" class="lyrics-translate-en font-en1 mt-1 size-14 translate-en">I can no longer recall the beginning.</p>
<p lang="zh-Hant" class="lyrics-translate-zh font-zh2 mt-1 translate-zh" style="">那是非常戲劇性的</p>
""";

        var result = MarumaruCrawlerService.ExtractLyricsFromHtml(html);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("はじまりはもう思い出せない", result[0].Japanese);
        Assert.Equal("開始已經回想不起來了", result[0].Chinese);
        Assert.Equal("それは とてもドラマチックで", result[1].Japanese);
        Assert.Equal("那是非常戲劇性的", result[1].Chinese);
    }

    [Fact]
    public void ExtractLyricsFromHtml_DoesNotTreatSongTitleTranslateZhAsLyricTranslation()
    {
        const string html = """
<h2 class="text-center size-16 translate-zh">無論如何無論如何</h2>
<select id="input-repeat-start">
  <option value="1">(1)光よすべて集まれ</option>
</select>
<p lang="zh-Hant" class="lyrics-translate-zh font-zh2 mt-1 translate-zh">光啊 全都聚集起來吧</p>
""";

        var result = MarumaruCrawlerService.ExtractLyricsFromHtml(html);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("光啊 全都聚集起來吧", result[0].Chinese);
    }

    [Fact]
    public void ExtractLyricsFromHtml_SkipsCreditLines()
    {
        const string html = """
<select id="input-repeat-start">
  <option value="1">(1)作詞：野田洋次郎</option>
  <option value="2">(2)光よすべて集まれ</option>
  <option value="3">(3)作曲：野田洋次郎</option>
</select>
<p lang="zh-Hant" class="lyrics-translate-zh font-zh2 mt-1 translate-zh">credit</p>
<p lang="zh-Hant" class="lyrics-translate-zh font-zh2 mt-1 translate-zh">光啊 全都聚集起來吧</p>
""";

        var result = MarumaruCrawlerService.ExtractLyricsFromHtml(html);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("光よすべて集まれ", result[0].Japanese);
        Assert.Equal("光啊 全都聚集起來吧", result[0].Chinese);
    }

    [Fact]
    public void AlignWithLrc_SplitsUnspacedChineseWhenLrcLinesAreSubphrases()
    {
        var service = new MarumaruCrawlerService(
            new ConfigurationBuilder().Build(),
            NullLogger<MarumaruCrawlerService>.Instance,
            new FakeHttpClientFactory());
        var marumaruLyrics = new List<(string Japanese, string Chinese)>
        {
            ("あたしあなたにあえて本当に嬉しいのに", "明明我真的很慶幸能遇上你")
        };
        var lrcLines = new List<(double TimeStamp, string Japanese)>
        {
            (14.1, "あたしあなたにあえて"),
            (18.2, "本当に嬉しいのに")
        };

        var result = service.AlignWithLrc(marumaruLyrics, lrcLines);

        Assert.Equal(2, result.Count);
        Assert.All(result, segment => Assert.False(string.IsNullOrWhiteSpace(segment.Chinese)));
        Assert.DoesNotContain(result, segment => segment.Chinese == "明明我真的很慶幸能遇上你");
        Assert.Equal("明明我真的很慶幸能遇上你", string.Concat(result.Select(segment => segment.Chinese)));
    }

    [Fact]
    public void AlignWithLrc_DoesNotUseNearDuplicateTranslationForShortChangedLine()
    {
        var service = new MarumaruCrawlerService(
            new ConfigurationBuilder().Build(),
            NullLogger<MarumaruCrawlerService>.Instance,
            new FakeHttpClientFactory());
        var marumaruLyrics = new List<(string Japanese, string Chinese)>
        {
            ("痛みの数だけ", "痛苦有多少")
        };
        var lrcLines = new List<(double TimeStamp, string Japanese)>
        {
            (100.2, "言葉の数だけ")
        };

        var result = service.AlignWithLrc(marumaruLyrics, lrcLines);

        Assert.Single(result);
        Assert.Equal("言葉の数だけ", result[0].Japanese);
        Assert.Equal(string.Empty, result[0].Chinese);
    }

    [Fact]
    public void AlignLyricsWithTimestamps_WhenAsrLineCountDiffers_PreservesMarumaruLineCount()
    {
        var service = new MarumaruCrawlerService(
            new ConfigurationBuilder().Build(),
            NullLogger<MarumaruCrawlerService>.Instance,
            new FakeHttpClientFactory());
        var marumaruLyrics = new List<(string Japanese, string Chinese)>
        {
            ("輪廻して", "輪迴著"),
            ("あなたに会うたび思います。", "每次遇見你時我都會想"),
            ("創造は", "創造是")
        };
        var timestampedSegments = new List<LyricSegment>
        {
            new() { TimeStamp = 11.78, Japanese = "リし" },
            new() { TimeStamp = 16.36, Japanese = "てあなたに会うに思います想像" }
        };

        var result = service.AlignLyricsWithTimestamps(marumaruLyrics, timestampedSegments);

        Assert.Equal(marumaruLyrics.Count, result.Count);
        Assert.Equal(new[] { "輪廻して", "あなたに会うたび思います。", "創造は" }, result.Select(segment => segment.Japanese));
        Assert.All(result, segment => Assert.False(string.IsNullOrWhiteSpace(segment.Chinese)));
        Assert.True(result.Zip(result.Skip(1), (left, right) => left.TimeStamp <= right.TimeStamp).All(isOrdered => isOrdered));
    }

    [Fact]
    public void AlignLyricsWithTimestamps_ShouldNotMatchShortLyricToDistantLowSimilarityAnchor()
    {
        var service = new MarumaruCrawlerService(
            new ConfigurationBuilder().Build(),
            NullLogger<MarumaruCrawlerService>.Instance,
            new FakeHttpClientFactory());
        var marumaruLyrics = new List<(string Japanese, string Chinese)>
        {
            ("輪廻して", "輪迴著"),
            ("あなたに会うたび思います。", "每次遇見你時我都會想"),
            ("創造は", "創造是"),
            ("乗り越えるための車輪だと。", "為了跨越而存在的車輪"),
            ("輪廻して", "輪迴著"),
            ("私の中にもあったこと。", "在我心中也曾存在過")
        };
        var timestampedSegments = new List<LyricSegment>
        {
            new() { TimeStamp = 12.4, Japanese = "リし" },
            new() { TimeStamp = 16.359, Japanese = "てあなたに会うに思います。想像" },
            new() { TimeStamp = 24.48, Japanese = "はうん" },
            new() { TimeStamp = 27.56, Japanese = "乗り越えるためのシ輪だとリし" },
            new() { TimeStamp = 35.96, Japanese = "てあなたに会うに思い" },
            new() { TimeStamp = 40.8, Japanese = "ます特別" },
            new() { TimeStamp = 46.84, Japanese = "私の中にもあった" },
            new() { TimeStamp = 70.88, Japanese = "にし" }
        };

        var result = service.AlignLyricsWithTimestamps(marumaruLyrics, timestampedSegments);

        Assert.Equal(marumaruLyrics.Count, result.Count);
        Assert.True(result.Zip(result.Skip(1), (left, right) => left.TimeStamp <= right.TimeStamp).All(isOrdered => isOrdered));
        Assert.True(result[4].TimeStamp < 70.88);
        Assert.Equal("輪廻して", result[4].Japanese);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
