using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class TypingTubeLyricsServiceTests
{
    [Fact]
    public void ParseLyricsTsv_ShouldKeepRubyBaseTextAndDropEndLine()
    {
        const string tsv = "title\thttps://youtu.be/m1o67ti-DME\n" +
            "2.310\tはいがんばっ<ruby>て<rt>てゃ</rt></ruby>\tはいがんばってゃ\n" +
            "4.202\tさあがんばれ～ぃ\tさあがんばれ～ぃ\n" +
            "110\tend\t\n";

        var result = TypingTubeLyricsService.ParseLyricsTsv(tsv);

        Assert.Equal(2, result.Count);
        Assert.Equal(2.310, result[0].TimeStamp, precision: 3);
        Assert.Equal("はいがんばって", result[0].Japanese);
        Assert.Equal("さあがんばれ～ぃ", result[1].Japanese);
    }

    [Fact]
    public void ExtractMovieIdsFromSearchHtml_ShouldExtractTypingTubeMovieIdsFromRedirectLinks()
    {
        const string html = "<a class=\"result__a\" href=\"//duckduckgo.com/l/?uddg=https%3A%2F%2Ftyping-tube.net%2Fmovie%2Fshow%2F69499\">歌詞</a>" +
            "<a href=\"https://typing-tube.net/movie/show/70000\">other</a>";

        var ids = TypingTubeLyricsService.ExtractMovieIdsFromSearchHtml(html);

        Assert.Contains(69499, ids);
        Assert.Contains(70000, ids);
    }
}
