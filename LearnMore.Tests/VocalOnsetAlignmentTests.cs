using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class VocalOnsetAlignmentTests
{
    [Fact]
    public void AlignLyricsToWordTimings_ShouldReturnPreciseLineStartsAndEnds()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("残酷な天使のように", 0.90),
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 7.28),
            new VocalOnsetDetectionService.LyricTimingSeed("蒼い風がいま胸のドアを叩いても", 23.17),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("残酷な", 0.91, 1.40),
            new VocalOnsetDetectionService.WhisperWordTiming("天使の", 1.41, 1.92),
            new VocalOnsetDetectionService.WhisperWordTiming("ように", 1.93, 2.31),
            new VocalOnsetDetectionService.WhisperWordTiming("少年よ", 7.30, 7.92),
            new VocalOnsetDetectionService.WhisperWordTiming("神話に", 7.93, 8.41),
            new VocalOnsetDetectionService.WhisperWordTiming("なれ", 8.42, 8.68),
            new VocalOnsetDetectionService.WhisperWordTiming("蒼い風が", 23.18, 24.00),
            new VocalOnsetDetectionService.WhisperWordTiming("いま胸の", 24.01, 24.82),
            new VocalOnsetDetectionService.WhisperWordTiming("ドアを", 24.83, 25.19),
            new VocalOnsetDetectionService.WhisperWordTiming("叩いても", 25.20, 25.88),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words);

        Assert.Collection(aligned,
            line =>
            {
                Assert.Equal("残酷な天使のように", line.Text);
                Assert.Equal(0.91, line.Start, 2);
                Assert.Equal(2.31, line.End, 2);
                Assert.True(line.IsMatched);
            },
            line =>
            {
                Assert.Equal("少年よ神話になれ", line.Text);
                Assert.Equal(7.30, line.Start, 2);
                Assert.Equal(8.68, line.End, 2);
                Assert.True(line.IsMatched);
            },
            line =>
            {
                Assert.Equal("蒼い風がいま胸のドアを叩いても", line.Text);
                Assert.Equal(23.18, line.Start, 2);
                Assert.Equal(25.88, line.End, 2);
                Assert.True(line.IsMatched);
            });
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldUseExpectedTimestampToAvoidMatchingEarlierRepeatedChorus()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 86.35),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("少年よ", 7.30, 7.92),
            new VocalOnsetDetectionService.WhisperWordTiming("神話に", 7.93, 8.41),
            new VocalOnsetDetectionService.WhisperWordTiming("なれ", 8.42, 8.68),
            new VocalOnsetDetectionService.WhisperWordTiming("少年よ", 86.36, 86.95),
            new VocalOnsetDetectionService.WhisperWordTiming("神話に", 86.96, 87.42),
            new VocalOnsetDetectionService.WhisperWordTiming("なれ", 87.43, 87.70),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.Equal(86.36, line.Start, 2);
        Assert.Equal(87.70, line.End, 2);
        Assert.True(line.IsMatched);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldHandleCharacterLevelWhisperTokensForLongJapaneseLine()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("蒼い風がいま胸のドアを叩いても", 23.17),
        };

        var chars = new[] { "蒼", "い", "風", "が", "い", "ま", "胸", "の", "ド", "ア", "を", "叩", "い", "て", "も" };
        var words = chars
            .Select((ch, index) => new VocalOnsetDetectionService.WhisperWordTiming(
                ch,
                23.18 + (index * 0.12),
                23.28 + (index * 0.12)))
            .ToArray();

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.Equal(23.18, line.Start, 2);
        Assert.Equal(24.96, line.End, 2);
        Assert.True(line.IsMatched);
    }

    [Fact]
    public void BuildComparableJapaneseText_ShouldPreserveKanaPrefixBeforeInlineReading()
    {
        var comparable = VocalOnsetDetectionService.BuildComparableJapaneseText("この宇宙(そら)を抱いて輝く");

        Assert.Equal("このそらを抱いて輝く", comparable);
    }

    [Fact]
    public void BuildComparableJapaneseText_ShouldPreferInlineKanaReadingAndTokenReadings()
    {
        var comparable = VocalOnsetDetectionService.BuildComparableJapaneseText(
            "この宇宙(そら)を抱いて輝く",
            new[]
            {
                new VocalOnsetDetectionService.PhoneticToken("この", "この"),
                new VocalOnsetDetectionService.PhoneticToken("そら", "そら"),
                new VocalOnsetDetectionService.PhoneticToken("を", "を"),
                new VocalOnsetDetectionService.PhoneticToken("抱いて", "だいて"),
                new VocalOnsetDetectionService.PhoneticToken("輝く", "かがやく"),
            });

        Assert.Equal("このそらをだいてかがやく", comparable);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldMatchPhoneticizedWordsForNearHomophoneAsrOutput()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed(
                VocalOnsetDetectionService.BuildComparableJapaneseText(
                    "少年よ神話になれ",
                    new[]
                    {
                        new VocalOnsetDetectionService.PhoneticToken("少年", "しょうねん"),
                        new VocalOnsetDetectionService.PhoneticToken("よ", "よ"),
                        new VocalOnsetDetectionService.PhoneticToken("神話", "しんわ"),
                        new VocalOnsetDetectionService.PhoneticToken("になれ", "になれ"),
                    }),
                87.31),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming(
                VocalOnsetDetectionService.BuildComparableJapaneseText(
                    "少年余震話になれ",
                    new[]
                    {
                        new VocalOnsetDetectionService.PhoneticToken("少年", "しょうねん"),
                        new VocalOnsetDetectionService.PhoneticToken("余震話", "よしんわ"),
                        new VocalOnsetDetectionService.PhoneticToken("になれ", "になれ"),
                    }),
                87.06,
                88.76),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(87.06, line.Start, 2);
        Assert.Equal(88.76, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldAcceptNearTimeCandidateDespiteAsrKanjiDrift()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("しょうねんよしんわになれ", 87.31),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("しょうねんよしんばなしになれ", 87.06, 88.76),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(87.06, line.Start, 2);
        Assert.Equal(88.76, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldFallbackToPrefixMatchForStartWhenTailAsrIsTooNoisy()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("しょうねんよしんわになれ", 87.31),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("しょうねんよじしんはみなれ", 87.06, 90.16),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(87.06, line.Start, 2);
        Assert.Equal(90.16, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldNotTreatShortContainedFragmentAsWholeLineMatch()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 87.31),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("少年よ", 87.06, 87.24),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 5);

        var line = Assert.Single(aligned);
        Assert.False(line.IsMatched);
        Assert.Equal(87.31, line.Start, 2);
        Assert.Equal(87.31, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldAcceptHighCoverageNearTimeNoisyLine()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしるあついぱとすでおもいでをうらぎるなら", 226.44),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("おとはしはつきはなつておもいでもうらぎるなら", 225.42, 232.66),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(226.44, line.Start, 2);
        Assert.Equal(232.66, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldClampVeryEarlyFirstTokenToExpectedStart()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("残酷な天使のように", 2.39),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("残", 0.00, 2.72),
            new VocalOnsetDetectionService.WhisperWordTiming("酷", 2.72, 3.56),
            new VocalOnsetDetectionService.WhisperWordTiming("な", 3.56, 4.78),
            new VocalOnsetDetectionService.WhisperWordTiming("天", 4.78, 5.34),
            new VocalOnsetDetectionService.WhisperWordTiming("使", 5.34, 5.78),
            new VocalOnsetDetectionService.WhisperWordTiming("の", 5.78, 6.38),
            new VocalOnsetDetectionService.WhisperWordTiming("ように", 6.38, 7.14),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(2.39, line.Start, 2);
        Assert.Equal(7.14, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldClampRescueMatchThatStartsTooEarlyBackToExpectedStart()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしるあついぱとすでおもいでをうらぎるなら", 226.44),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("おとはしはつきはなつておもいでもうらぎるなら", 225.42, 232.66),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(226.44, line.Start, 2);
        Assert.Equal(232.66, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldClampLowScoreNearHomophoneMatchWhenStartLeadsExpectedTooMuch()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("しょうねんよしんわになれ", 237.49),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("しょうねんよしばなしになる", 236.62, 240.32),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        var line = Assert.Single(aligned);
        Assert.True(line.IsMatched);
        Assert.Equal(237.49, line.Start, 2);
        Assert.Equal(240.32, line.End, 2);
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldNotLetPreviousLineConsumeNextLinePrefix()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("おもいでをうらぎるなら", 79.89),
            new VocalOnsetDetectionService.LyricTimingSeed("このそらをだいてかがやく", 83.89),
            new VocalOnsetDetectionService.LyricTimingSeed("しょうねんよしんわになれ", 86.02),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("おもいでを", 79.20, 80.10),
            new VocalOnsetDetectionService.WhisperWordTiming("うらぎるなら", 80.10, 82.66),
            new VocalOnsetDetectionService.WhisperWordTiming("この", 82.80, 83.84),
            new VocalOnsetDetectionService.WhisperWordTiming("そらを", 83.84, 85.10),
            new VocalOnsetDetectionService.WhisperWordTiming("だいて", 85.10, 85.70),
            new VocalOnsetDetectionService.WhisperWordTiming("かがやく", 85.70, 86.86),
            new VocalOnsetDetectionService.WhisperWordTiming("しょうねんよ", 87.06, 88.76),
            new VocalOnsetDetectionService.WhisperWordTiming("よしんはになれ", 88.76, 90.32),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        Assert.Collection(aligned,
            line =>
            {
                Assert.True(line.IsMatched);
                Assert.Equal(79.20, line.Start, 2);
                Assert.Equal(82.66, line.End, 2);
            },
            line =>
            {
                Assert.True(line.IsMatched);
                Assert.Equal(82.80, line.Start, 2);
                Assert.Equal(86.86, line.End, 2);
            },
            line =>
            {
                Assert.True(line.IsMatched);
                Assert.Equal(87.06, line.Start, 2);
                Assert.Equal(90.32, line.End, 2);
            });
    }

    [Fact]
    public void AlignLyricsToWordTimings_ShouldKeepHighConfidenceTailChorusStartsAtWordBoundary()
    {
        var lyrics = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("このそらをだいてかがやく", 233.10),
            new VocalOnsetDetectionService.LyricTimingSeed("しょうねんよしんわになれ", 237.49),
        };

        var words = new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("この", 233.19, 233.58),
            new VocalOnsetDetectionService.WhisperWordTiming("そら", 233.58, 234.52),
            new VocalOnsetDetectionService.WhisperWordTiming("を", 234.52, 234.96),
            new VocalOnsetDetectionService.WhisperWordTiming("だいて", 234.96, 235.58),
            new VocalOnsetDetectionService.WhisperWordTiming("かがやく", 235.58, 236.62),
            new VocalOnsetDetectionService.WhisperWordTiming("しょうねん", 236.62, 238.08),
            new VocalOnsetDetectionService.WhisperWordTiming("よしんは", 238.08, 239.48),
            new VocalOnsetDetectionService.WhisperWordTiming("になれ", 239.48, 240.32),
        };

        var aligned = VocalOnsetDetectionService.AlignLyricsToWordTimings(lyrics, words, expectedSearchPaddingSeconds: 8);

        Assert.Collection(aligned,
            line =>
            {
                Assert.True(line.IsMatched);
                Assert.Equal(233.19, line.Start, 2);
                Assert.Equal(236.62, line.End, 2);
            },
            line =>
            {
                Assert.True(line.IsMatched);
                Assert.Equal(236.62, line.Start, 2);
                Assert.Equal(240.32, line.End, 2);
            });
    }

    [Fact]
    public void EvaluateSecondaryAlignmentSegment_ShouldApproveHotobashiruSecondOpinionStart()
    {
        var signal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "ほとばしる熱いパトスで思い出を裏切るなら",
            "言葉しる熱いパクスで 思い出を裏切るなら",
            75.0,
            83.0);

        Assert.True(signal.FullSimilarity >= 0.80);
        Assert.True(signal.PrefixSimilarity >= 0.58);
        Assert.True(signal.SuffixSimilarity >= 0.93);
        Assert.True(VocalOnsetDetectionService.ShouldUseSecondaryAlignmentStart(77.28, signal));
    }

    [Fact]
    public void EvaluateSecondaryAlignmentSegment_ShouldRejectKonosoraCrossLineSegment()
    {
        var signal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "この宇宙(そら)を抱いて輝く",
            "この空を抱いて輝く 少年 余震はになれ",
            83.0,
            90.6);

        Assert.True(signal.FullSimilarity < 0.80 || signal.SuffixSimilarity < 0.93);
        Assert.False(VocalOnsetDetectionService.ShouldUseSecondaryAlignmentStart(82.80, signal));
    }

    [Fact]
    public void EvaluateSecondaryAlignmentSegment_ShouldRejectShonenCrossLineSegment()
    {
        var signal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "少年よ神話になれ",
            "この空を抱いて輝く 少年 余震はになれ",
            83.0,
            90.6);

        Assert.True(signal.FullSimilarity < 0.80 || signal.PrefixSimilarity < 0.58 || signal.SuffixSimilarity < 0.93);
        Assert.False(VocalOnsetDetectionService.ShouldUseSecondaryAlignmentStart(87.06, signal));
    }

    [Fact]
    public void ApplySecondaryAlignmentStartHints_ShouldPullHotobashiruEarlierWhenSignalPassesThreshold()
    {
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment(
                "ほとばしる熱いパトスで思い出を裏切るなら",
                76.56,
                77.28,
                82.80,
                0.913,
                true,
                115,
                129),
        };

        var signal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "ほとばしる熱いパトスで思い出を裏切るなら",
            "言葉しる熱いパクスで 思い出を裏切るなら",
            75.0,
            83.0);

        var updated = VocalOnsetDetectionService.ApplySecondaryAlignmentStartHints(
            alignments,
            new Dictionary<int, VocalOnsetDetectionService.SecondaryAlignmentSignal> { [0] = signal });

        var line = Assert.Single(updated);
        Assert.Equal(75.0, line.Start, 2);
        Assert.Equal(82.80, line.End, 2);
    }

    [Fact]
    public void ApplySecondaryAlignmentStartHints_ShouldConvertUnmatchedLineWhenSignalPassesThreshold()
    {
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment(
                "ほとばしる熱いパトスで思い出を裏切るなら",
                76.56,
                76.56,
                76.56,
                0.458,
                false,
                -1,
                -1),
        };

        var signal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "ほとばしる熱いパトスで思い出を裏切るなら",
            "言葉しる熱いパクスで 思い出を裏切るなら",
            75.0,
            83.0);

        var updated = VocalOnsetDetectionService.ApplySecondaryAlignmentStartHints(
            alignments,
            new Dictionary<int, VocalOnsetDetectionService.SecondaryAlignmentSignal> { [0] = signal });

        var line = Assert.Single(updated);
        Assert.True(line.IsMatched);
        Assert.Equal(75.0, line.Start, 2);
        Assert.Equal(83.0, line.End, 2);
        Assert.Equal(signal.FullSimilarity, line.Score, 3);
    }

    [Fact]
    public void ApplySecondaryAlignmentStartHints_ShouldIgnoreCrossLineControlSignals()
    {
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment(
                "この宇宙(そら)を抱いて輝く",
                83.67,
                82.80,
                86.88,
                1.341,
                true,
                130,
                136),
            new VocalOnsetDetectionService.LyricTimingAlignment(
                "少年よ神話になれ",
                87.31,
                87.06,
                90.58,
                0.908,
                true,
                137,
                144),
        };

        var konosoraSignal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "この宇宙(そら)を抱いて輝く",
            "この空を抱いて輝く 少年 余震はになれ",
            83.0,
            90.6);
        var shonenSignal = VocalOnsetDetectionService.EvaluateSecondaryAlignmentSegment(
            "少年よ神話になれ",
            "この空を抱いて輝く 少年 余震はになれ",
            83.0,
            90.6);

        var updated = VocalOnsetDetectionService.ApplySecondaryAlignmentStartHints(
            alignments,
            new Dictionary<int, VocalOnsetDetectionService.SecondaryAlignmentSignal>
            {
                [0] = konosoraSignal,
                [1] = shonenSignal,
            });

        Assert.Equal(82.80, updated[0].Start, 2);
        Assert.Equal(87.06, updated[1].Start, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldSelectOnlyHotobashiruSignal()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
            new VocalOnsetDetectionService.LyricTimingSeed("この宇宙(そら)を抱いて輝く", 83.67),
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 87.31),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 77.28, 82.80, 0.913, true, 115, 129),
            new VocalOnsetDetectionService.LyricTimingAlignment("この宇宙(そら)を抱いて輝く", 83.67, 82.80, 86.88, 1.341, true, 130, 136),
            new VocalOnsetDetectionService.LyricTimingAlignment("少年よ神話になれ", 87.31, 87.06, 90.58, 0.908, true, 137, 144),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(75.0, 83.0, "言葉しる熱いパクスで 思い出を裏切るなら"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(83.0, 90.6, "この空を抱いて輝く 少年 余震はになれ"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(75.0, hints[0].Start, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldBuildHintForUnmatchedHotobashiruLine()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 76.56, 76.56, 0.458, false, -1, -1),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(75.0, 83.0, "言葉しる熱いパクスで 思い出を裏切るなら"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(75.0, hints[0].Start, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldCombineAdjacentSegmentsForHotobashiruSignal()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 77.28, 82.80, 0.913, true, 115, 129),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(75.56, 79.56, "言葉散るはすげばすで"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(79.56, 83.56, "思い出を裏にるなら"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(83.56, 86.56, "この空を抱いてかまや"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(75.56, hints[0].Start, 2);
    }

    [Fact]
    public void BuildFocusedSecondaryAlignmentWindows_ShouldCreateFocusedWindowForUnmatchedLine8()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("残酷な天使のテーゼ窓辺からやがて飛び立つ", 68.30),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("残酷な天使のテーゼ窓辺からやがて飛び立つ", 68.30, 68.30, 68.30, 0.390, false, -1, -1),
        };

        var windows = VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments);

        Assert.Single(windows);
        var window = windows[0];
        Assert.Equal(65.30, window.Start, 2);
        Assert.Equal(74.00, window.End, 2);
    }

    [Fact]
    public void BuildFocusedSecondaryAlignmentWindows_ShouldMergeRepeatedShortClusterIntoLongerContextWindowObservedOnLiveFallback()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("はい、頑張って。じゃ、頑張れ。はい", 2.52),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
            new VocalOnsetDetectionService.LyricTimingSeed("あ、もう頑張って", 21.68),
            new VocalOnsetDetectionService.LyricTimingSeed("いいよ。頑張ったら頑張ってもなきゃ", 24.28),
            new VocalOnsetDetectionService.LyricTimingSeed("に頑張れっていうのは恐縮けれども君は", 29.48),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 32.64),
            new VocalOnsetDetectionService.LyricTimingSeed("この距離で頑張ってて", 84.00),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("はい、頑張って。じゃ、頑張れ。はい", 2.52, 2.52, 2.52, 0.126, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.199, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("あ、もう頑張って", 21.68, 21.68, 21.68, 0.164, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("いいよ。頑張ったら頑張ってもなきゃ", 24.28, 24.28, 24.28, 0.329, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("に頑張れっていうのは恐縮けれども君は", 29.48, 29.48, 29.48, 0.250, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 32.64, 32.02, 32.64, 1.344, true, 10, 14),
            new VocalOnsetDetectionService.LyricTimingAlignment("この距離で頑張ってて", 84.00, 84.00, 84.00, -0.005, false, -1, -1),
        };

        var windows = VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments);

        Assert.Equal(2, windows.Count);
        Assert.Equal(3.96, windows[0].Start, 2);
        Assert.Equal(35.18, windows[0].End, 2);
        Assert.Equal(81.00, windows[1].Start, 2);
        Assert.Equal(89.70, windows[1].End, 2);
    }

    [Fact]
    public void BuildFocusedSecondaryAlignmentWindows_ShouldCreateWindowForRepeatedShortLowScoreSeedsObservedOnLiveFallback()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
            new VocalOnsetDetectionService.LyricTimingSeed("あ、もう頑張って", 21.68),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 32.64),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.199, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("あ、もう頑張って", 21.68, 21.68, 21.68, 0.164, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 32.64, 32.02, 32.64, 1.344, true, 10, 14),
        };

        var windows = VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments);

        Assert.Single(windows);
        Assert.Equal(3.96, windows[0].Start, 2);
        Assert.Equal(20.78, windows[0].End, 2);
    }

    [Fact]
    public void BuildFocusedSecondaryAlignmentWindows_ShouldAddTailWindowForLastUnmatchedLineObservedOnLiveFallback()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("はい、頑張って。じゃ、頑張れ。はい", 2.52),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
            new VocalOnsetDetectionService.LyricTimingSeed("あ、もう頑張って", 21.68),
            new VocalOnsetDetectionService.LyricTimingSeed("いいよ。頑張ったら頑張ってもなきゃ", 24.28),
            new VocalOnsetDetectionService.LyricTimingSeed("に頑張れっていうのは恐縮けれども君は", 29.48),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 32.64),
            new VocalOnsetDetectionService.LyricTimingSeed("この距離で頑張ってて", 84.00),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("はい、頑張って。じゃ、頑張れ。はい", 2.52, 2.52, 2.52, 0.126, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.199, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("あ、もう頑張って", 21.68, 21.68, 21.68, 0.164, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("いいよ。頑張ったら頑張ってもなきゃ", 24.28, 24.28, 24.28, 0.329, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("に頑張れっていうのは恐縮けれども君は", 29.48, 29.48, 29.48, 0.250, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 32.64, 32.02, 32.64, 1.344, true, 10, 14),
            new VocalOnsetDetectionService.LyricTimingAlignment("この距離で頑張ってて", 84.00, 84.00, 84.00, -0.005, false, -1, -1),
        };

        var windows = VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments);

        Assert.Equal(2, windows.Count);
        Assert.Equal(3.96, windows[0].Start, 2);
        Assert.Equal(35.18, windows[0].End, 2);
        Assert.Equal(81.00, windows[1].Start, 2);
        Assert.Equal(89.70, windows[1].End, 2);
    }

    [Fact]
    public void BuildFocusedSecondaryAlignmentWindows_ShouldCreateWindowForRepeatedShortUnmatchedSeeds()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 32.64),
            new VocalOnsetDetectionService.LyricTimingSeed("この距離で頑張ってて", 84.00),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.34, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.33, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 32.64, 32.64, 32.64, 0.34, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("この距離で頑張ってて", 84.00, 84.00, 86.20, 0.91, true, 10, 14),
        };

        var windows = VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments);

        Assert.Equal(2, windows.Count);
        Assert.Equal(3.96, windows[0].Start, 2);
        Assert.Equal(20.78, windows[0].End, 2);
        Assert.Equal(29.64, windows[1].Start, 2);
        Assert.Equal(38.34, windows[1].End, 2);
    }

    [Fact]
    public void HasViableFocusedSecondaryAlignmentCandidate_ShouldRejectPercussionLikeSegmentsObservedOnLiveFallback()
    {
        var window = new VocalOnsetDetectionService.SecondaryAlignmentWindow(3.96, 12.66);
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.199, false, -1, -1),
        };
        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(3.96, 5.96, "コンボディ"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(12.08, 20.68, "ドンッドンッ カンカンッ カンパッケー シャッ ドンッ ドゥッ ドンッ カンパッケー ドンッ ドゥッ ドゥッ"),
        };

        Assert.False(VocalOnsetDetectionService.HasViableFocusedSecondaryAlignmentCandidate(window, segments, seeds, alignments));
    }

    [Fact]
    public void HasViableFocusedSecondaryAlignmentCandidate_ShouldAcceptFocusedWindowWithActualLyricLikeSegment()
    {
        var window = new VocalOnsetDetectionService.SecondaryAlignmentWindow(3.96, 12.66);
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
        };
        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(6.10, 7.20, "がんばって"),
        };

        Assert.True(VocalOnsetDetectionService.HasViableFocusedSecondaryAlignmentCandidate(window, segments, seeds, alignments));
    }

    [Fact]
    public void HasSecondaryAlignmentWork_ShouldBeTrueWhenOnlyFocusedRepeatedShortWindowsExist()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("はい、頑張って。じゃ、頑張れ。はい", 2.52),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 6.96),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 15.08),
            new VocalOnsetDetectionService.LyricTimingSeed("あ、もう頑張って", 21.68),
            new VocalOnsetDetectionService.LyricTimingSeed("いいよ。頑張ったら頑張ってもなきゃ", 24.28),
            new VocalOnsetDetectionService.LyricTimingSeed("に頑張れっていうのは恐縮けれども君は", 29.48),
            new VocalOnsetDetectionService.LyricTimingSeed("頑張って", 32.64),
            new VocalOnsetDetectionService.LyricTimingSeed("この距離で頑張ってて", 84.00),
        };
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("はい、頑張って。じゃ、頑張れ。はい", 2.52, 2.52, 2.52, 0.126, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 6.96, 6.96, 6.96, 0.186, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 15.08, 15.08, 15.08, 0.199, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("あ、もう頑張って", 21.68, 21.68, 21.68, 0.164, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("いいよ。頑張ったら頑張ってもなきゃ", 24.28, 24.28, 24.28, 0.329, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("に頑張れっていうのは恐縮けれども君は", 29.48, 29.48, 29.48, 0.250, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("頑張って", 32.64, 32.02, 32.64, 1.344, true, 10, 14),
            new VocalOnsetDetectionService.LyricTimingAlignment("この距離で頑張ってて", 84.00, 84.00, 84.00, -0.005, false, -1, -1),
        };

        Assert.Empty(VocalOnsetDetectionService.BuildSecondaryAlignmentWindows(seeds, alignments));
        Assert.Equal(2, VocalOnsetDetectionService.BuildFocusedSecondaryAlignmentWindows(seeds, alignments).Count);
        Assert.True(VocalOnsetDetectionService.HasSecondaryAlignmentWork(seeds, alignments));
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldRescueUnmatchedLine6FromRawSingleSegment()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("だけどいつか気付くでしょうその背中には", 53.35),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("だけどいつか気付くでしょうその背中には", 53.35, 53.35, 53.35, 0.546, false, -1, -1),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(50.35, 59.55, "何でも一か傷くでしょう その手中にも"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(50.35, hints[0].Start, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldRescueHarukaMiraiFromRawSingleSegment()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("遥か未来めざすための羽根があること", 60.94),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("遥か未来めざすための羽根があること", 60.94, 60.94, 66.54, 0.672, true, 90, 102),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(59.55, 66.95, "春か未来でさすための雨があること"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(59.55, hints[0].Start, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldRescueUnmatchedHotobashiruFromRawSplitSegments()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 76.56, 76.56, 0.458, false, -1, -1),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(73.56, 78.34, "僕たちを飛ばしずはつきな風せ"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(78.34, 81.44, "思い出を裏にくら"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Single(hints);
        Assert.True(hints.ContainsKey(0));
        Assert.Equal(73.56, hints[0].Start, 2);
    }

    [Fact]
    public void ApplySecondaryAlignmentStartHints_ShouldRescueLine5FromLiveObservedSignal()
    {
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("に頑張れっていうのは恐縮けれども君は", 29.48, 29.48, 29.48, 0.250, false, -1, -1),
        };

        var signals = new Dictionary<int, VocalOnsetDetectionService.SecondaryAlignmentSignal>
        {
            [0] = new VocalOnsetDetectionService.SecondaryAlignmentSignal(
                27.96,
                32.78,
                "頑張ってるやつに頑張れ!ってような教室暗きじゃども頑張って!",
                0.321,
                0.167,
                0.062,
                3,
                0.222,
                0.000,
                0.000)
        };

        var updated = VocalOnsetDetectionService.ApplySecondaryAlignmentStartHints(alignments, signals);

        var line = Assert.Single(updated);
        Assert.True(line.IsMatched);
        Assert.Equal(27.96, line.Start, 2);
        Assert.Equal(32.78, line.End, 2);
    }

    [Fact]
    public void ApplySecondaryAlignmentStartHints_ShouldNotRescueLine0FromLateRepeatedShortCandidate()
    {
        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("はい、頑張って。じゃ、頑張れ。はい", 2.52, 2.52, 2.52, 0.126, false, -1, -1),
        };

        var signals = new Dictionary<int, VocalOnsetDetectionService.SecondaryAlignmentSignal>
        {
            [0] = new VocalOnsetDetectionService.SecondaryAlignmentSignal(
                5.96,
                9.96,
                "頑張って!頑張って!",
                0.462,
                0.250,
                0.125,
                2,
                0.308,
                0.000,
                0.000)
        };

        var updated = VocalOnsetDetectionService.ApplySecondaryAlignmentStartHints(alignments, signals);

        var line = Assert.Single(updated);
        Assert.False(line.IsMatched);
        Assert.Equal(2.52, line.Start, 2);
        Assert.Equal(2.52, line.End, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentHints_ShouldNotPullShonenEarlierFromSplitCrossLineSegments()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 87.31),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("少年よ神話になれ", 87.31, 87.06, 90.58, 0.908, true, 137, 144),
        };

        var segments = new[]
        {
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(83.56, 86.56, "この空を抱いてかまや"),
            new VocalOnsetDetectionService.SecondaryAlignmentSegment(86.56, 90.56, "少年余震はになれ"),
        };

        var hints = VocalOnsetDetectionService.BuildSecondaryAlignmentHints(seeds, alignments, segments);

        Assert.Empty(hints);
    }

    [Fact]
    public void BuildSecondaryAlignmentWindows_ShouldSelectOnlyLateStartLines()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
            new VocalOnsetDetectionService.LyricTimingSeed("この宇宙(そら)を抱いて輝く", 83.67),
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 87.31),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 77.28, 82.80, 0.913, true, 115, 129),
            new VocalOnsetDetectionService.LyricTimingAlignment("この宇宙(そら)を抱いて輝く", 83.67, 82.80, 86.88, 1.341, true, 130, 136),
            new VocalOnsetDetectionService.LyricTimingAlignment("少年よ神話になれ", 87.31, 87.06, 90.58, 0.908, true, 137, 144),
        };

        var windows = VocalOnsetDetectionService.BuildSecondaryAlignmentWindows(seeds, alignments);

        var window = Assert.Single(windows);
        Assert.Equal(73.56, window.Start, 2);
        Assert.Equal(86.30, window.End, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentWindows_ShouldMergeOverlappingLateStartWindows()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("A", 50.0),
            new VocalOnsetDetectionService.LyricTimingSeed("B", 57.0),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("A", 50.0, 51.0, 55.0, 0.9, true, 0, 0),
            new VocalOnsetDetectionService.LyricTimingAlignment("B", 57.0, 58.0, 62.0, 0.9, true, 0, 0),
        };

        var windows = VocalOnsetDetectionService.BuildSecondaryAlignmentWindows(seeds, alignments);

        var window = Assert.Single(windows);
        Assert.Equal(47.0, window.Start, 2);
        Assert.Equal(65.5, window.End, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentWindows_ShouldAlsoSelectLongModerateScoreLineNearSeed()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
            new VocalOnsetDetectionService.LyricTimingSeed("この宇宙(そら)を抱いて輝く", 83.67),
            new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 87.31),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 76.56, 82.80, 0.93, true, 115, 129),
            new VocalOnsetDetectionService.LyricTimingAlignment("この宇宙(そら)を抱いて輝く", 83.67, 83.67, 86.88, 1.34, true, 130, 136),
            new VocalOnsetDetectionService.LyricTimingAlignment("少年よ神話になれ", 87.31, 87.06, 90.58, 0.91, true, 137, 144),
        };

        var windows = VocalOnsetDetectionService.BuildSecondaryAlignmentWindows(seeds, alignments);

        var window = Assert.Single(windows);
        Assert.Equal(73.56, window.Start, 2);
        Assert.Equal(86.30, window.End, 2);
    }

    [Fact]
    public void BuildSecondaryAlignmentWindows_ShouldSelectUnmatchedHotobashiruRiskLine()
    {
        var seeds = new[]
        {
            new VocalOnsetDetectionService.LyricTimingSeed("運命さえまだ知らないいたいけな瞳", 46.13),
            new VocalOnsetDetectionService.LyricTimingSeed("だけどいつか気付くでしょうその背中には", 53.35),
            new VocalOnsetDetectionService.LyricTimingSeed("遥か未来めざすための羽根があること", 60.94),
            new VocalOnsetDetectionService.LyricTimingSeed("残酷な天使のテーゼ窓辺からやがて飛び立つ", 68.30),
            new VocalOnsetDetectionService.LyricTimingSeed("ほとばしる熱いパトスで思い出を裏切るなら", 76.56),
        };

        var alignments = new[]
        {
            new VocalOnsetDetectionService.LyricTimingAlignment("運命さえまだ知らないいたいけな瞳", 46.13, 44.56, 49.86, 0.777, true, 0, 0),
            new VocalOnsetDetectionService.LyricTimingAlignment("だけどいつか気付くでしょうその背中には", 53.35, 53.35, 53.35, 0.546, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("遥か未来めざすための羽根があること", 60.94, 60.94, 66.54, 0.672, true, 0, 0),
            new VocalOnsetDetectionService.LyricTimingAlignment("残酷な天使のテーゼ窓辺からやがて飛び立つ", 68.30, 68.30, 68.30, 0.390, false, -1, -1),
            new VocalOnsetDetectionService.LyricTimingAlignment("ほとばしる熱いパトスで思い出を裏切るなら", 76.56, 76.56, 76.56, 0.458, false, -1, -1),
        };

        var windows = VocalOnsetDetectionService.BuildSecondaryAlignmentWindows(seeds, alignments);

        Assert.Equal(2, windows.Count);
        Assert.Equal(50.35, windows[0].Start, 2);
        Assert.Equal(70.04, windows[0].End, 2);
        Assert.Equal(73.56, windows[1].Start, 2);
        Assert.Equal(81.56, windows[1].End, 2);
    }
}
