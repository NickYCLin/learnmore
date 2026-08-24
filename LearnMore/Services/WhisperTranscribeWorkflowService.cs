using System.Diagnostics;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace LearnMore.Services;

public class WhisperTranscribeWorkflowService : IWhisperTranscribeWorkflowService
{
    private readonly OpenAiWhisperClientService _openAiClient;
    private readonly IWhisperSongPersistenceService _songPersistence;
    private readonly WhisperRuntimeOptions _options;

    public WhisperTranscribeWorkflowService(
        OpenAiWhisperClientService openAiClient,
        IWhisperSongPersistenceService songPersistence,
        IOptions<WhisperRuntimeOptions> options)
    {
        _openAiClient = openAiClient;
        _songPersistence = songPersistence;
        _options = options.Value;
    }

    public async Task<string> ExecuteAsync(TranscribeRequest request)
    {
        var audioFilePath = await DownloadYouTubeAudioAsync(request.YouTubeUrl);
        if (string.IsNullOrEmpty(audioFilePath))
        {
            throw new Exception("Failed to download audio from YouTube.");
        }

        try
        {
            var transcription = await _openAiClient.TranscribeAudioAsync(audioFilePath, request.Language);
            string songUid = await _songPersistence.AddSongToDatabaseAsync(request);
            await _songPersistence.CreateDynamicSongTableAsync(songUid);
            await _songPersistence.InsertTranscriptionToDynamicTableAsync(songUid, transcription);
            return transcription;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(audioFilePath) && File.Exists(audioFilePath))
            {
                File.Delete(audioFilePath);
            }
        }
    }

    private async Task<string> DownloadYouTubeAudioAsync(string youTubeUrl)
    {
        var youtube = new YoutubeClient();
        var video = await youtube.Videos.GetAsync(youTubeUrl);
        var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);
        var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        if (audioStreamInfo == null)
        {
            throw new Exception("No audio streams available for this video.");
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{audioStreamInfo.Container.Name}");
        var outputFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

        await youtube.Videos.Streams.DownloadAsync(audioStreamInfo, tempFilePath);

        var ffmpegArgs = $"-i \"{tempFilePath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 -map_metadata -1 -fflags +bitexact -flags:v +bitexact -flags:a +bitexact \"{outputFilePath}\"";
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = ffmpegArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        using var ffmpegCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(ffmpegCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception("FFmpeg conversion timed out after 60 seconds.");
        }

        if (process.ExitCode != 0)
        {
            throw new Exception($"FFmpeg conversion failed: {await process.StandardError.ReadToEndAsync()}");
        }

        return outputFilePath;
    }
}
