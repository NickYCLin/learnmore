using System.Diagnostics;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public class YtDlpAudioDownloaderService
{
    private readonly WhisperRuntimeOptions _options;
    private readonly ILogger<YtDlpAudioDownloaderService> _logger;

    public YtDlpAudioDownloaderService(
        IOptions<WhisperRuntimeOptions> options,
        ILogger<YtDlpAudioDownloaderService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string GetYtDlpExecutablePath()
        => string.IsNullOrWhiteSpace(_options.YtDlpPath) ? "yt-dlp" : _options.YtDlpPath;

    public string GetFfmpegExecutablePath()
        => _options.FfmpegPath;

    public string ResolveDownloadedAudioPath(string requestedOutputFilePath)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputFilePath) && File.Exists(requestedOutputFilePath))
            return requestedOutputFilePath;

        if (string.IsNullOrWhiteSpace(requestedOutputFilePath))
            return requestedOutputFilePath;

        var directory = Path.GetDirectoryName(requestedOutputFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return requestedOutputFilePath;

        var requestedName = Path.GetFileName(requestedOutputFilePath);
        var stem = Path.GetFileNameWithoutExtension(requestedOutputFilePath);
        var candidates = Directory.GetFiles(directory, stem + "*")
            .OrderBy(path => string.Equals(Path.GetFileName(path), requestedName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .ToList();

        return candidates.FirstOrDefault(File.Exists) ?? requestedOutputFilePath;
    }

    public virtual Task<string> DownloadAudioAsync(string youTubeUrl, bool extractAudioAsMp3)
        => DownloadAudioCoreAsync(youTubeUrl, extractAudioAsMp3);

    private async Task<string> DownloadAudioCoreAsync(string youTubeUrl, bool extractAudioAsMp3)
    {
        var ytDlpPath = GetYtDlpExecutablePath();
        var ffmpegPath = GetFfmpegExecutablePath();
        var outputFilePath = extractAudioAsMp3
            ? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mp3")
            : Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.%(ext)s");

        var ytDlpArgs = extractAudioAsMp3
            ? $"-f bestaudio --extract-audio --audio-format mp3 --ffmpeg-location \"{ffmpegPath}\" --audio-quality 0 -o \"{outputFilePath}\" \"{youTubeUrl}\""
            : $"-f bestaudio --ffmpeg-location \"{ffmpegPath}\" -o \"{outputFilePath}\" \"{youTubeUrl}\"";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = ytDlpArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogInformation("開始以 yt-dlp 下載音訊，extractAudioAsMp3={ExtractAudioAsMp3}", extractAudioAsMp3);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timeoutSeconds = Math.Max(30, _options.YtDlpDownloadTimeoutSeconds);
        using var ytDlpCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(ytDlpCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception($"yt-dlp download timed out after {timeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new Exception($"yt-dlp failed: stdout={stdout}; stderr={stderr}");
        }

        return ResolveDownloadedAudioPath(outputFilePath);
    }
}
