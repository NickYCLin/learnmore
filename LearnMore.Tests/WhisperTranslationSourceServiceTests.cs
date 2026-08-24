using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using Xunit;

namespace LearnMore.Tests;

public class WhisperTranslationSourceServiceTests
{
    [Fact]
    public async Task ResolveFinalSegmentsAsync_WhenCrawlerSourcesUnavailable_UsesGptBatchTranslationBeforeFallback()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "你好@世界"
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "こんにちは", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "世界", Chinese = "翻譯中..." }
            },
            preAlignedSegments: null);

        Assert.Equal(TranslationSourceKind.Gpt, result.Source);
        Assert.Equal(new[] { "你好", "世界" }, result.Segments.Select(segment => segment.Chinese));
        Assert.Equal("こんにちは@世界", openAiClient.LastBatchInput);
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WhenRuntimeOpenAiTranslationDisabled_DoesNotCallGptFallback()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "你好@世界"
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = false }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "こんにちは", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "世界", Chinese = "翻譯中..." }
            },
            preAlignedSegments: null);

        Assert.Equal(TranslationSourceKind.Fallback, result.Source);
        Assert.Equal(new[] { "翻譯中...", "翻譯中..." }, result.Segments.Select(segment => segment.Chinese));
        Assert.Null(openAiClient.LastBatchInput);
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WhenGptBatchShapeMismatch_KeepsExplicitFallbackPlaceholders()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "只有一行"
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "こんにちは", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "世界", Chinese = "翻譯中..." }
            },
            preAlignedSegments: null);

        Assert.Equal(TranslationSourceKind.Fallback, result.Source);
        Assert.All(result.Segments, segment => Assert.Equal("翻譯中...", segment.Chinese));
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WithPartiallyTranslatedPreAlignedSegments_UsesGptForMissingLines()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "話語有多少"
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "翻譯中..." }
            },
            preAlignedSegments: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "痛苦有多少" },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "" }
            });

        Assert.Equal(TranslationSourceKind.PreAligned, result.Source);
        Assert.Equal(new[] { "痛苦有多少", "話語有多少" }, result.Segments.Select(segment => segment.Chinese));
        Assert.Equal("言葉の数だけ", openAiClient.LastBatchInput);
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WithDuplicateChineseForDifferentShortLines_RetranslatesSuspiciousLines()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "痛苦有多少@話語有多少"
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "翻譯中..." }
            },
            preAlignedSegments: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "痛苦有多少" },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "痛苦有多少" }
            });

        Assert.Equal(TranslationSourceKind.PreAligned, result.Source);
        Assert.Equal(new[] { "痛苦有多少", "話語有多少" }, result.Segments.Select(segment => segment.Chinese));
        Assert.Equal("痛みの数だけ@言葉の数だけ", openAiClient.LastBatchInput);
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WhenBatchKeepsDuplicateShortLineTranslations_UsesLineByLineFallback()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = "痛苦有多少@痛苦有多少",
            LineTranslations = new Dictionary<string, string>
            {
                ["痛みの数だけ"] = "痛苦有多少",
                ["言葉の数だけ"] = "痛苦有多少"
            }
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "翻譯中..." }
            },
            preAlignedSegments: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "痛苦有多少" },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "痛苦有多少" }
            });

        Assert.Equal(TranslationSourceKind.PreAligned, result.Source);
        Assert.Equal(new[] { "痛苦有多少", "話語有多少" }, result.Segments.Select(segment => segment.Chinese));
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WhenMissingTranslationBatchFails_UsesLineByLineFallback()
    {
        var openAiClient = new FakeOpenAiWhisperClientService
        {
            BatchTranslateResult = null,
            LineTranslations = new Dictionary<string, string>
            {
                ["言葉の数だけ"] = "話語有多少"
            }
        };
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance,
            openAiWhisperClient: openAiClient,
            options: OptionsFactory.Create(new WhisperRuntimeOptions { EnableRuntimeOpenAiTranslation = true }));

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "翻譯中..." }
            },
            preAlignedSegments: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "痛苦有多少" },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "" }
            });

        Assert.Equal(TranslationSourceKind.PreAligned, result.Source);
        Assert.Equal(new[] { "痛苦有多少", "話語有多少" }, result.Segments.Select(segment => segment.Chinese));
        Assert.Equal("言葉の数だけ", openAiClient.LastBatchInput);
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WithPartialPreAlignedSegmentsAndNoGpt_DoesNotReturnBlankTranslations()
    {
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance);

        var result = await service.ResolveFinalSegmentsAsync(
            title: string.Empty,
            artist: string.Empty,
            stableSegmentsToInsert: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "翻譯中..." },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "翻譯中..." }
            },
            preAlignedSegments: new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "痛みの数だけ", Chinese = "痛苦有多少" },
                new() { TimeStamp = 2.0, Japanese = "言葉の数だけ", Chinese = "" }
            });

        Assert.Equal(TranslationSourceKind.Fallback, result.Source);
        Assert.Equal(new[] { "翻譯中...", "翻譯中..." }, result.Segments.Select(segment => segment.Chinese));
    }

    [Theory]
    [InlineData(20, 30, true)]
    [InlineData(30, 34, false)]
    [InlineData(0, 12, true)]
    [InlineData(5, 7, false)]
    public void ShouldPreferFormalLyricLineCount_OnlySwitchesWhenFormalLyricsAreClearlyMoreComplete(
        int timestampLineCount,
        int formalLyricLineCount,
        bool expected)
    {
        var actual = WhisperTranslationSourceService.ShouldPreferFormalLyricLineCount(
            timestampLineCount,
            formalLyricLineCount);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TryPreAlignAsync_WithCancelledToken_ThrowsBeforeCrawlerWork()
    {
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.TryPreAlignAsync(
                "title",
                "artist",
                new List<LyricSegment>
                {
                    new() { TimeStamp = 1.0, Japanese = "こんにちは" }
                },
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ResolveFinalSegmentsAsync_WithCancelledTokenAndCrawlerLookupNeeded_ThrowsBeforeCrawlerWork()
    {
        var service = new WhisperTranslationSourceService(
            marumaruCrawlerService: null!,
            logger: NullLogger<WhisperTranslationSourceService>.Instance);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ResolveFinalSegmentsAsync(
                "title",
                "artist",
                new List<LyricSegment>
                {
                    new() { TimeStamp = 1.0, Japanese = "こんにちは", Chinese = "翻譯中..." }
                },
                preAlignedSegments: null,
                cancellationTokenSource.Token));
    }

    private sealed class FakeOpenAiWhisperClientService : IOpenAiWhisperClientService
    {
        public string? BatchTranslateResult { get; init; }
        public IReadOnlyDictionary<string, string>? LineTranslations { get; init; }
        public string? LastBatchInput { get; private set; }

        public Task<string> TranscribeAudioAsync(string audioFilePath, string language) => Task.FromResult("{}");

        public Task<string?> BatchTranslateToChineseAsync(string combinedJapanese)
        {
            LastBatchInput = combinedJapanese;
            return Task.FromResult(BatchTranslateResult);
        }

        public Task<string> TranslateToChineseAsync(string japaneseText)
            => Task.FromResult(LineTranslations != null && LineTranslations.TryGetValue(japaneseText, out var translated)
                ? translated
                : japaneseText);

        public Task<string?> TranslateSongTitleToTraditionalChineseAsync(string songTitle, string? artist = null)
            => Task.FromResult<string?>(songTitle);

        public Task<(string RubyText, string ChineseText)> ProcessJapaneseTextAsync(string text) => Task.FromResult((text, text));
    }
}
