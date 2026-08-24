using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public class WhisperAudioPreprocessServiceTests
{
    [Fact]
    public async Task TrimLeadingSilenceAsync_WithCancelledToken_ThrowsBeforeStartingFfmpeg()
    {
        var service = CreateService();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.TrimLeadingSilenceAsync("/tmp/audio.mp3", cancellationTokenSource.Token));
    }

    [Fact]
    public async Task TrimLeadingSilenceAsync_WithMissingSourceFile_ReturnsOriginalPathAndZeroOffset()
    {
        var service = CreateService();
        var missingPath = "/tmp/does-not-exist-audio.mp3";

        var result = await service.TrimLeadingSilenceAsync(missingPath, CancellationToken.None);

        Assert.Equal(missingPath, result.AudioFilePath);
        Assert.Equal(0.0, result.TrimOffsetSeconds);
    }

    private static WhisperAudioPreprocessService CreateService()
    {
        return new WhisperAudioPreprocessService(
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()),
            NullLogger<WhisperAudioPreprocessService>.Instance);
    }
}
