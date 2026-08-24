using LearnMore.Models;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class WhisperTranscriptionPersistenceServiceTests
{
    [Fact]
    public async Task ParseTranscriptionToSegmentsAsync_ShouldUseOpenAiClientToPopulateRubyAndChinese()
    {
        var service = new WhisperTranscriptionPersistenceService(
            new FakeOpenAiWhisperClientService
            {
                ProcessJapaneseTextResult = ("<ruby>世界<rt>せかい</rt></ruby>", "世界")
            });

        var segments = (await service.ParseTranscriptionToSegmentsAsync("""
            {"segments":[{"start":1.5,"text":"世界"}]}
            """)).ToList();

        var segment = Assert.Single(segments);
        Assert.Equal(1.5, segment.TimeStamp);
        Assert.Equal("世界", segment.Japanese);
        Assert.Equal("世界", segment.Chinese);
        Assert.Equal("<ruby>世界<rt>せかい</rt></ruby>", segment.JapaneseRuby);
    }

    [Fact]
    public async Task ParseTranscriptionToSegmentsChineseAsync_ShouldUseBatchTranslatorResultsWhenLineCountsMatch()
    {
        var service = new WhisperTranscriptionPersistenceService(
            new FakeOpenAiWhisperClientService
            {
                BatchTranslateResult = "第一行中文@第二行中文"
            });

        var segments = (await service.ParseTranscriptionToSegmentsChineseAsync("""
            {"segments":[
                {"start":0.1,"text":"第一行"},
                {"start":0.2,"text":"第二行"}
            ]}
            """)).ToList();

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(0.1, first.TimeStamp);
                Assert.Equal("第一行", first.Japanese);
                Assert.Equal("第一行中文", first.Chinese);
            },
            second =>
            {
                Assert.Equal(0.2, second.TimeStamp);
                Assert.Equal("第二行", second.Japanese);
                Assert.Equal("第二行中文", second.Chinese);
            });
    }

    [Fact]
    public async Task ParseTranscriptionToSegmentsChineseAsync_ShouldFallBackToPerLineTranslationWhenBatchCountMismatches()
    {
        var service = new WhisperTranscriptionPersistenceService(
            new FakeOpenAiWhisperClientService
            {
                BatchTranslateResult = "只有一行中文",
                TranslateResults =
                {
                    ["第一行"] = "單行一",
                    ["第二行"] = "單行二"
                }
            });

        var segments = (await service.ParseTranscriptionToSegmentsChineseAsync("""
            {"segments":[
                {"start":0.1,"text":"第一行"},
                {"start":0.2,"text":"第二行"}
            ]}
            """)).ToList();

        Assert.Collection(
            segments,
            first => Assert.Equal("單行一", first.Chinese),
            second => Assert.Equal("單行二", second.Chinese));
    }

    private sealed class FakeOpenAiWhisperClientService : IOpenAiWhisperClientService
    {
        public (string RubyText, string ChineseText) ProcessJapaneseTextResult { get; init; } = (string.Empty, string.Empty);
        public string? BatchTranslateResult { get; init; }
        public Dictionary<string, string> TranslateResults { get; } = new();

        public Task<string> TranscribeAudioAsync(string audioFilePath, string language) => Task.FromResult("{}");

        public Task<string?> BatchTranslateToChineseAsync(string combinedJapanese) => Task.FromResult(BatchTranslateResult);

        public Task<string> TranslateToChineseAsync(string japaneseText)
            => Task.FromResult(TranslateResults.TryGetValue(japaneseText, out var translated) ? translated : string.Empty);

        public Task<string?> TranslateSongTitleToTraditionalChineseAsync(string songTitle, string? artist = null)
            => Task.FromResult<string?>(songTitle);

        public Task<(string RubyText, string ChineseText)> ProcessJapaneseTextAsync(string text)
            => Task.FromResult(ProcessJapaneseTextResult);
    }
}
