using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class YouTubeCommandSafetyTests
{
    private const string VideoId = "dQw4w9WgXcQ";

    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=3")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=test")]
    public void NormalizeWatchUrl_ShouldReturnCanonicalUrl(string input)
    {
        Assert.Equal(
            $"https://www.youtube.com/watch?v={VideoId}",
            YouTubeVideoIdExtractor.NormalizeWatchUrl(input));
    }

    [Theory]
    [InlineData("--exec=calc")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.attacker.invalid/watch?v=dQw4w9WgXcQ")]
    public void NormalizeWatchUrl_ShouldRejectOptionsAndSpoofedHosts(string input)
    {
        Assert.Null(YouTubeVideoIdExtractor.NormalizeWatchUrl(input));
    }

    [Fact]
    public async Task AudioDownloader_ShouldRejectInvalidInputBeforeStartingYtDlp()
    {
        var service = new YtDlpAudioDownloaderService(
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()),
            NullLogger<YtDlpAudioDownloaderService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DownloadAudioAsync("--exec=calc", extractAudioAsMp3: true));
    }

    [Fact]
    public void YtDlpServices_ShouldUseArgumentListAndOptionTerminator()
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("Services", "YtDlpAudioDownloaderService.cs"),
            Path.Combine("Services", "YouTubeMetadataResolverService.cs"),
            Path.Combine("Services", "YouTubeSubtitleDownloadService.cs"),
            Path.Combine("Services", "DemucsAudioStemProcessor.cs")
        })
        {
            var source = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "LearnMore",
                relativePath));

            Assert.Contains("NormalizeWatchUrl", source);
            Assert.Contains("ArgumentList.Add", source);
            Assert.Contains("\"--\"", source);
            Assert.DoesNotContain("Arguments =", source);
        }
    }

    [Fact]
    public void MediaController_ShouldNormalizeYouTubeInputBeforeMetadataLookup()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore", "Controllers", "MediaController.cs"));
        var normalization = source.IndexOf(
            "YouTubeVideoIdExtractor.NormalizeWatchUrl(request.YouTubeUrl)",
            StringComparison.Ordinal);
        var metadataLookup = source.IndexOf(
            "_youTubeMetadataResolverService.ResolveAsync(",
            StringComparison.Ordinal);

        Assert.True(normalization >= 0);
        Assert.True(metadataLookup > normalization);
        Assert.Contains("request.YouTubeUrl = normalizedYouTubeUrl", source);
    }
}
