using LearnMore.Controllers;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class MediaControllerPrecisionMessageTests
{
    [Fact]
    public void WebTranscribe_ShouldUseVocalOnsetInitialSegmentationFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        Assert.Contains("_vocalOnsetService.TranscribeInitialSegmentsAsync", source);
    }

    [Fact]
    public void WebTranscribe_ShouldCreateEditableSongEvenWhenFallbackSegmentsAreEmpty()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        Assert.DoesNotContain("await SendEvent(\"done\", new { songUid = string.Empty });", source);
        Assert.Contains("未取得可用時間戳，將建立空白歌詞供手動編輯", source);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldExtractCleanJapaneseSongTitleAndDiscardLabelArtist()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "「残酷な天使のテーゼ」MUSIC VIDEO（HDver.）/Zankoku na Tenshi no Te-ze“The Cruel Angel's Thesis”",
            "KING RECORDS");

        Assert.NotNull(normalized);
        Assert.Equal("残酷な天使のテーゼ", normalized.Value.Title);
        Assert.Null(normalized.Value.Artist);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldKeepUsefulTopicArtist()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "残酷な天使のテーゼ",
            "高橋洋子 - Topic");

        Assert.NotNull(normalized);
        Assert.Equal("残酷な天使のテーゼ", normalized.Value.Title);
        Assert.Equal("高橋洋子", normalized.Value.Artist);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldStripDanglingBracketedArtistSuffixFromTitle()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "頑張りたいソング【轟はじめ - おだまよ",
            "轟はじめ");

        Assert.NotNull(normalized);
        Assert.Equal("頑張りたいソング", normalized.Value.Title);
        Assert.Equal("轟はじめ", normalized.Value.Artist);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldStripLastDanglingBracketSuffixEvenAfterBalancedPrefix()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "Song [Live] extra [broken",
            "Test Artist");

        Assert.NotNull(normalized);
        Assert.Equal("Song [Live] extra", normalized.Value.Title);
        Assert.Equal("Test Artist", normalized.Value.Artist);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldKeepMixedWidthBalancedParentheses()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "Song (Live） extra",
            "Test Artist");

        Assert.NotNull(normalized);
        Assert.Equal("Song extra", normalized.Value.Title);
        Assert.Equal("Test Artist", normalized.Value.Artist);
    }

    [Fact]
    public void TryNormalizeYouTubeMetadata_ShouldKeepMixedWidthBalancedParenthesesWhenOpenBracketIsFullWidth()
    {
        var normalized = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(
            "Song（Live) extra",
            "Test Artist");

        Assert.NotNull(normalized);
        Assert.Equal("Song extra", normalized.Value.Title);
        Assert.Equal("Test Artist", normalized.Value.Artist);
    }

    [Fact]
    public void BuildPrecisionCorrectionWarningMessage_ShouldIncludeReasonAndDetailSnippet()
    {
        var message = MediaController.BuildPrecisionCorrectionWarningMessage(
            "openai_non_success_status",
            "{\"error\":{\"message\":\"Incorrect API key provided\"}}");

        Assert.Contains("openai_non_success_status", message);
        Assert.Contains("Incorrect API key provided", message);
    }

    [Fact]
    public void BuildPrecisionCorrectionWarningMessage_ShouldTrimLongDetail()
    {
        var detail = new string('a', 200);

        var message = MediaController.BuildPrecisionCorrectionWarningMessage(
            "local_faster_whisper_process_failed",
            detail);

        Assert.StartsWith("⚠️ 精準校正未取得完整結果（local_faster_whisper_process_failed：", message);
        Assert.Contains("...", message);
        Assert.True(message.Length < 140);
    }
}
