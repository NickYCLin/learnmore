using System.Diagnostics;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public class YouTubeSubtitleDownloadService : IYouTubeSubtitleDownloadService
{
    private readonly WhisperRuntimeOptions _options;
    private readonly YouTubeSubtitleParserService _parser;
    private readonly ILogger<YouTubeSubtitleDownloadService> _logger;

    public YouTubeSubtitleDownloadService(
        IOptions<WhisperRuntimeOptions> options,
        YouTubeSubtitleParserService parser,
        ILogger<YouTubeSubtitleDownloadService> logger)
    {
        _options = options.Value;
        _parser = parser;
        _logger = logger;
    }

    public async Task<List<LyricSegment>?> TryDownloadSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        => await TryDownloadSubtitleFileAsync(
            youTubeUrl,
            "--write-sub --sub-lang ja",
            "ja",
            cancellationToken);

    public async Task<List<LyricSegment>?> TryDownloadTranslationSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        => await TryDownloadSubtitleFileAsync(
            youTubeUrl,
            "--write-sub --sub-langs zh-TW,zh-Hant",
            "zh-TW",
            cancellationToken);

    public async Task<List<LyricSegment>?> TryDownloadAutoCaptionTimeAnchorsAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        => await TryDownloadSubtitleFileAsync(
            youTubeUrl,
            "--write-auto-subs --sub-langs ja-orig,ja",
            "ja-orig",
            cancellationToken);

    private async Task<List<LyricSegment>?> TryDownloadSubtitleFileAsync(
        string youTubeUrl,
        string subtitleArgs,
        string preferredLanguage,
        CancellationToken cancellationToken)
    {
        var ytDlpPath = string.IsNullOrWhiteSpace(_options.YtDlpPath) ? "yt-dlp" : _options.YtDlpPath;
        var tempDir = Path.GetTempPath();
        var guid = Guid.NewGuid().ToString();

        try
        {
            var cookiesPath = _options.YtDlpCookiesPath;
            var cookiesArg = !string.IsNullOrEmpty(cookiesPath) && File.Exists(cookiesPath)
                ? $"--cookies \"{cookiesPath}\" "
                : string.Empty;
            var args = $"{cookiesArg}{subtitleArgs} --sub-format vtt --skip-download --no-write-playlist-metafiles --ignore-errors -o \"{Path.Combine(tempDir, guid)}\" \"{youTubeUrl}\"";

            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation("開始以 yt-dlp 下載 YouTube 字幕，PreferredLanguage={PreferredLanguage}", preferredLanguage);

                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var timeoutSeconds = Math.Max(30, _options.YtDlpDownloadTimeoutSeconds);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    _logger.LogWarning("yt-dlp 字幕下載逾時，TimeoutSeconds={TimeoutSeconds}", timeoutSeconds);
                    return null;
                }

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "yt-dlp 字幕下載失敗，ExitCode={ExitCode}, StdoutLength={StdoutLength}, StderrLength={StderrLength}",
                        process.ExitCode,
                        stdout.Length,
                        stderr.Length);
                    return null;
                }
            }

            var vttFilePath = Directory
                .EnumerateFiles(tempDir, $"{guid}*.vtt")
                .OrderByDescending(path => Path.GetFileName(path).Contains(preferredLanguage, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(vttFilePath) || !File.Exists(vttFilePath))
            {
                return null;
            }

            var lines = await File.ReadAllLinesAsync(vttFilePath, cancellationToken);
            var segments = _parser.ParseSegments(lines);
            _logger.LogInformation("YouTube 字幕解析完成，PreferredLanguage={PreferredLanguage}, SegmentCount={SegmentCount}", preferredLanguage, segments.Count);
            return segments.Count == 0 ? null : segments;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube 字幕下載或解析失敗");
            return null;
        }
        finally
        {
            foreach (var filePath in Directory.EnumerateFiles(tempDir, $"{guid}*.vtt"))
            {
                File.Delete(filePath);
            }
        }
    }
}
