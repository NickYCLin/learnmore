using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class AudioStemBackgroundQueueTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void Program_ShouldRegisterAudioStemBackgroundQueue()
    {
        var source = Source("LearnMore", "Program.cs");

        Assert.Contains("Configure<AudioStemProcessingOptions>", source);
        Assert.Contains("AddScoped<IAudioStemJobService, AudioStemJobService>", source);
        Assert.Contains("AddScoped<DemucsAudioStemProcessor>", source);
        Assert.Contains("AddScoped<RemoteApiAudioStemProcessor>", source);
        Assert.Contains("AddScoped<IAudioStemProcessor>(sp =>", source);
        Assert.Contains("options.UseRemoteApi", source);
        Assert.Contains("GetRequiredService<RemoteApiAudioStemProcessor>", source);
        Assert.Contains("GetRequiredService<DemucsAudioStemProcessor>", source);
        Assert.Contains("AddHostedService<AudioStemProcessingHostedService>", source);
    }

    [Fact]
    public void SongPersistence_ShouldEnqueueAudioStemsAfterSongCreation()
    {
        var source = Source("LearnMore", "Services", "WhisperSongPersistenceService.cs");

        Assert.Contains("IAudioStemJobService", source);
        Assert.Contains("EnqueueAudioStemJobSafeAsync", source);
        Assert.Contains("request.YouTubeUrl", source);
        Assert.Contains("request.YouTubeLink", source);
        Assert.Contains("伴奏/人聲背景隊列入隊失敗", source);
    }

    [Fact]
    public void AudioStemJobService_ShouldCreateDurableRetryQueue()
    {
        var source = Source("LearnMore", "Services", "AudioStemJobService.cs");

        Assert.Contains("SongAudioStemJobs", source);
        Assert.Contains("UX_SongAudioStemJobs_SongUid", source);
        Assert.Contains("IX_SongAudioStemJobs_Status_NextAttemptAt", source);
        Assert.Contains("AttemptCount = AttemptCount + 1", source);
        Assert.Contains("LockedUntil = DATEADD", source);
        Assert.Contains("Status = CASE WHEN AttemptCount >= MaxAttempts THEN N'dead' ELSE N'failed' END", source);
        Assert.Contains("RegisterCompletedStemsAsync", source);
    }

    [Fact]
    public void DemucsProcessor_ShouldDownloadSeparateConvertAndRegisterStems()
    {
        var source = Source("LearnMore", "Services", "DemucsAudioStemProcessor.cs");

        Assert.Contains("--extract-audio", source);
        Assert.Contains("--ffmpeg-location", source);
        Assert.Contains("startInfo.Environment[\"PATH\"]", source);
        Assert.Contains("\"demucs\"", source);
        Assert.Contains("--two-stems", source);
        Assert.Contains("instrumental.flac", source);
        Assert.Contains("vocals.flac", source);
        Assert.Contains("RegisterCompletedStemsAsync", source);
        Assert.Contains("background-demucs", source);
    }
}
