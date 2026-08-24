using System.Diagnostics;
using System.Runtime.InteropServices;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public class WhisperAudioPreprocessService : IWhisperAudioPreprocessService
{
    private readonly WhisperRuntimeOptions _options;
    private readonly ILogger<WhisperAudioPreprocessService> _logger;

    public WhisperAudioPreprocessService(
        IOptions<WhisperRuntimeOptions> options,
        ILogger<WhisperAudioPreprocessService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WhisperAudioPreprocessResult> TrimLeadingSilenceAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            return new WhisperAudioPreprocessResult(audioFilePath, 0.0);
        }

        var trimOffset = 0.0;
        var trimmedAudioPath = audioFilePath + "_trimmed.mp3";
        var ffmpegPath = string.IsNullOrWhiteSpace(_options.FfmpegPath) ? "ffmpeg" : _options.FfmpegPath;
        var ffprobePath = ResolveFfprobePath(ffmpegPath);

        try
        {
            var ffmpegArgs = $"-y -i \"{audioFilePath}\" -af silenceremove=start_periods=1:start_duration=0.3:start_threshold=-50dB \"{trimmedAudioPath}\"";

            using var ffmpegProc = new Process();
            ffmpegProc.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ffmpegProc.Start();
            _ = await ffmpegProc.StandardError.ReadToEndAsync();
            await ffmpegProc.WaitForExitAsync(cancellationToken);

            if (ffmpegProc.ExitCode != 0 || !File.Exists(trimmedAudioPath))
            {
                _logger.LogWarning("ffmpeg 靜音裁切失敗（exit {Code}），使用原始音訊", ffmpegProc.ExitCode);
                DeleteIfExists(trimmedAudioPath);
                return new WhisperAudioPreprocessResult(audioFilePath, 0.0);
            }

            var origDuration = await GetAudioDurationAsync(audioFilePath, ffprobePath, cancellationToken);
            var trimDuration = await GetAudioDurationAsync(trimmedAudioPath, ffprobePath, cancellationToken);
            trimOffset = Math.Max(0.0, origDuration - trimDuration);

            _logger.LogInformation(
                "ffmpeg 靜音裁切成功：原始 {Orig:F2}s，裁後 {Trim:F2}s，trimOffset={Offset:F2}s",
                origDuration,
                trimDuration,
                trimOffset);

            DeleteIfExists(audioFilePath);
            return new WhisperAudioPreprocessResult(trimmedAudioPath, trimOffset);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(trimmedAudioPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg 靜音裁切例外，使用原始音訊");
            DeleteIfExists(trimmedAudioPath);
            return new WhisperAudioPreprocessResult(audioFilePath, 0.0);
        }
    }

    private static string ResolveFfprobePath(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return "ffprobe";
        }

        if (string.Equals(ffmpegPath, "ffmpeg", StringComparison.OrdinalIgnoreCase) || string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        }

        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        }

        var ffprobeFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        return Path.Combine(directory, ffprobeFileName);
    }

    private static async Task<double> GetAudioDurationAsync(string filePath, string ffprobePath, CancellationToken cancellationToken)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(cancellationToken);

            if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var duration))
            {
                return duration;
            }
        }
        catch
        {
            // ffprobe 不可用時靜默忽略，offset = 0
        }

        return 0.0;
    }

    private static void DeleteIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
