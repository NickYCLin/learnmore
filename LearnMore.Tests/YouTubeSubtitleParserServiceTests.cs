using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class YouTubeSubtitleParserServiceTests
{
    [Fact]
    public void ParseVttSegments_ShouldParseYouTubeAutoCaptionIncrementalCueShape()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "Kind: captions",
            "Language: ja",
            "",
            "00:00:04.420 --> 00:00:12.390 align:start position:0%",
            " ",
            "[音楽]",
            "",
            "00:00:12.390 --> 00:00:12.400 align:start position:0%",
            " ",
            " ",
            "",
            "00:00:12.400 --> 00:00:16.349 align:start position:0%",
            " ",
            "最初<00:00:13.160><c>の</c><00:00:13.760><c>句</c>",
            "",
            "00:00:16.349 --> 00:00:16.359 align:start position:0%",
            " ",
            " ",
            "",
            "00:00:16.359 --> 00:00:24.470 align:start position:0%",
            " ",
            "次<00:00:17.359><c>の</c><00:00:18.039><c>歌詞</c>",
            "",
            "00:00:24.470 --> 00:00:24.480 align:start position:0%",
            "次の歌詞",
            " ",
            "",
            "00:00:24.480 --> 00:00:27.550 align:start position:0%",
            "次の歌詞",
            "続き<00:00:25.480><c>の</c><00:00:25.680><c>句</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(12.4, first.TimeStamp);
                Assert.Equal("最初の句", first.Japanese);
            },
            second =>
            {
                Assert.Equal(16.359, second.TimeStamp);
                Assert.Equal("次の歌詞", second.Japanese);
            },
            third =>
            {
                Assert.Equal(24.48, third.TimeStamp);
                Assert.Equal("続きの句", third.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldKeepOnlyLatestCueTextAndSkipDuplicateNoise()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>舊句子</c>",
            "<c>新句子</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>新句子</c>",
            "<c>新句子</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>[音楽]</c>",
            "",
            "00:00:07.000 --> 00:00:09.000 align:start position:0%",
            "<c>第二句</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("新句子", first.Japanese);
            },
            second =>
            {
                Assert.Equal(7.0, second.TimeStamp);
                Assert.Equal("第二句", second.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldSkipVeryShortTransitionCue()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:01.050 align:start position:0%",
            "<c>過渡句</c>",
            "",
            "00:00:02.000 --> 00:00:03.000 align:start position:0%",
            "<c>有效句</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(2.0, segment.TimeStamp);
        Assert.Equal("有效句", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldRemoveInlineMusicTagsAndPreserveNonConsecutiveRepeatedChorus()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>頑張って。</c><00:00:01.500><c>[音楽]</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>別の句</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>頑張って。</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("頑張って", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("別の句", second.Japanese);
            },
            third =>
            {
                Assert.Equal(5.0, third.TimeStamp);
                Assert.Equal("頑張って", third.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldDropNumericAndShortAsciiNoise()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>6543</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>H</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(3.0, segment.TimeStamp);
        Assert.Equal("この距離で頑張ってて", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldDropCountdownNoise()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>秒数えてみるとあ、7654321</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(3.0, segment.TimeStamp);
        Assert.Equal("この距離で頑張ってて", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldKeepLegitimateQuestionAndYearReference()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>どうして君は来ないの？</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>2024年の夢を見てる</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("どうして君は来ないの", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("2024年の夢を見てる", second.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldKeepShortDateAndEmbeddedNumberReference()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>2024年</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>彼は6543人目</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("2024年", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("彼は6543人目", second.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldKeepKanaOnlyNumericPhraseAndTrailingIdentifier()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>パート12</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>商品番号は76543</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>商品番号は 76543</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("パート12", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("商品番号は76543", second.Japanese);
            },
            third =>
            {
                Assert.Equal(5.0, third.TimeStamp);
                Assert.Equal("商品番号は 76543", third.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldKeepSingleCharacterJapaneseLyric()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>光</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>あ</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>ー</c>",
            "",
            "00:00:07.000 --> 00:00:09.000 align:start position:0%",
            "<c>々</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("光", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("あ", second.Japanese);
            },
            third =>
            {
                Assert.Equal(5.0, third.TimeStamp);
                Assert.Equal("ー", third.Japanese);
            },
            fourth =>
            {
                Assert.Equal(7.0, fourth.TimeStamp);
                Assert.Equal("々", fourth.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldPreserveRepeatedLyricSeparatedByDroppedNoise()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>頑張って</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>6543</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>頑張って</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("頑張って", first.Japanese);
            },
            second =>
            {
                Assert.Equal(5.0, second.TimeStamp);
                Assert.Equal("頑張って", second.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldDropObservedLowConfidenceHallucinationFragments()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>頑張って。ちょんちん</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>頑張ってドンピン</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>頑張って</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(5.0, segment.TimeStamp);
        Assert.Equal("頑張って", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldDropObservedSentenceLevelHallucinationFragments()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>頑張ってるやつちょ</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>あもう1回頑張って3分かずお</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>にゃあぶんボンボ</c>",
            "",
            "00:00:07.000 --> 00:00:09.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(7.0, segment.TimeStamp);
        Assert.Equal("この距離で頑張ってて", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldTrimObservedLongTailHallucinationSuffix()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>頑張ってちょ頑張っていただかないと10</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp);
                Assert.Equal("頑張って", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.0, second.TimeStamp);
                Assert.Equal("この距離で頑張ってて", second.Japanese);
            });
    }

    [Fact]
    public void ParseVttSegments_ShouldDropObservedLongSentenceHallucinationBlock()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>回頑張って。もうめどめんの早くない?もうちょっと頑張ってみてはいかがでしょうか?はい。じゃ、行け。ひ、頑張り。おお、お。早々にさっき諦めてない。諦めてなれちゅ。頑張って</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(3.0, segment.TimeStamp);
        Assert.Equal("この距離で頑張ってて", segment.Japanese);
    }

    [Fact]
    public void ParseVttSegments_ShouldDropObservedShortPhraseHallucinationAndQuestionTail()
    {
        var service = new YouTubeSubtitleParserService();
        var lines = new[]
        {
            "WEBVTT",
            "",
            "00:00:01.000 --> 00:00:03.000 align:start position:0%",
            "<c>ちゃ当たるもう1回はいはいもう1回</c>",
            "",
            "00:00:03.000 --> 00:00:05.000 align:start position:0%",
            "<c>1回1置いた方がいいのかな?あ、ハに</c>",
            "",
            "00:00:05.000 --> 00:00:07.000 align:start position:0%",
            "<c>この距離で頑張ってて</c>"
        };

        var segments = service.ParseSegments(lines).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(5.0, segment.TimeStamp);
        Assert.Equal("この距離で頑張ってて", segment.Japanese);
    }
}
