using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public sealed class DemucsAudioStemProcessor : IAudioStemProcessor
{
    private static readonly Regex SafeSongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly AudioStemProcessingOptions _options;
    private readonly IAudioStemJobService _jobService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DemucsAudioStemProcessor> _logger;

    public DemucsAudioStemProcessor(
        IOptions<AudioStemProcessingOptions> options,
        IAudioStemJobService jobService,
        IWebHostEnvironment environment,
        ILogger<DemucsAudioStemProcessor> logger)
    {
        _options = options.Value;
        _jobService = jobService;
        _environment = environment;
        _logger = logger;
    }

    public async Task ProcessAsync(AudioStemJob job, CancellationToken cancellationToken = default)
    {
        if (!SafeSongUidPattern.IsMatch(job.SongUid))
        {
            throw new InvalidOperationException("Invalid SongUid for audio stem processing.");
        }

        string workRoot = string.IsNullOrWhiteSpace(_options.WorkRoot)
            ? Path.Combine(Path.GetTempPath(), "learnmore-audio-stems")
            : _options.WorkRoot;
        string jobRoot = Path.Combine(workRoot, job.SongUid);
        if (Directory.Exists(jobRoot))
        {
            Directory.Delete(jobRoot, recursive: true);
        }
        Directory.CreateDirectory(jobRoot);

        try
        {
            string sourceAudio = await DownloadAudioAsync(job, jobRoot, cancellationToken);
            var (instrumental, vocals) = await SeparateAudioAsync(sourceAudio, jobRoot, cancellationToken);
            string destinationRoot = Path.Combine(_environment.WebRootPath, "audio-stems", job.SongUid);
            Directory.CreateDirectory(destinationRoot);

            string instrumentalDestination = Path.Combine(destinationRoot, "instrumental.flac");
            string vocalsDestination = Path.Combine(destinationRoot, "vocals.flac");
            File.Copy(instrumental, instrumentalDestination, overwrite: true);
            File.Copy(vocals, vocalsDestination, overwrite: true);

            await _jobService.RegisterCompletedStemsAsync(
                job.SongUid,
                instrumentalDestination,
                vocalsDestination,
                _options.ModelName,
                "background-demucs",
                cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(jobRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理音軌處理暫存資料夾失敗 {JobRoot}", jobRoot);
            }
        }
    }

    private async Task<string> DownloadAudioAsync(AudioStemJob job, string jobRoot, CancellationToken cancellationToken)
    {
        string outputTemplate = Path.Combine(jobRoot, "source.%(ext)s");
        var arguments = new List<string>
        {
            "--no-playlist",
            "--extract-audio",
            "--audio-format",
            "wav",
            "--audio-quality",
            "0",
            "--output",
            outputTemplate,
            "--print",
            "after_move:filepath",
            job.YouTubeVideoUrl
        };

        if (!string.IsNullOrWhiteSpace(_options.YtDlpCookiesPath) && File.Exists(_options.YtDlpCookiesPath))
        {
            arguments.InsertRange(0, new[] { "--cookies", _options.YtDlpCookiesPath });
        }

        string ffmpegLocation = ResolveFfmpegLocation(_options.FfmpegPath);
        if (!string.IsNullOrWhiteSpace(ffmpegLocation))
        {
            arguments.InsertRange(0, new[] { "--ffmpeg-location", ffmpegLocation });
        }

        var result = await RunProcessAsync(
            ResolveRequiredPath(_options.YtDlpPath, "yt-dlp"),
            arguments,
            jobRoot,
            TimeSpan.FromSeconds(Math.Max(30, _options.DownloadTimeoutSeconds)),
            cancellationToken,
            ffmpegLocation);

        foreach (string line in result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            string candidate = line.Trim();
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string fallback = Path.Combine(jobRoot, "source.wav");
        if (File.Exists(fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException("yt-dlp did not produce an audio file.");
    }

    private async Task<(string Instrumental, string Vocals)> SeparateAudioAsync(string audioPath, string jobRoot, CancellationToken cancellationToken)
    {
        string outputRoot = Path.Combine(jobRoot, "demucs");
        var arguments = new List<string>
        {
            "-m",
            "demucs",
            "--two-stems",
            "vocals",
            "--name",
            string.IsNullOrWhiteSpace(_options.ModelName) ? "htdemucs" : _options.ModelName,
            "--segment",
            _options.SegmentSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "--jobs",
            _options.Jobs.ToString(CultureInfo.InvariantCulture),
            "--shifts",
            _options.Shifts.ToString(CultureInfo.InvariantCulture),
            "--mp3",
            "--mp3-bitrate",
            "320",
            "--out",
            outputRoot,
            audioPath
        };

        if (!string.IsNullOrWhiteSpace(_options.Device))
        {
            arguments.InsertRange(2, new[] { "--device", _options.Device });
        }

        await RunProcessAsync(
            ResolveRequiredPath(_options.PythonPath, "python"),
            arguments,
            jobRoot,
            TimeSpan.FromSeconds(Math.Max(60, _options.SeparationTimeoutSeconds)),
            cancellationToken,
            ResolveFfmpegLocation(_options.FfmpegPath));

        var audioFiles = Directory.EnumerateFiles(outputRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? vocals = audioFiles.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), "vocals", StringComparison.OrdinalIgnoreCase));
        string? instrumental = audioFiles.FirstOrDefault(path =>
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            return string.Equals(stem, "no_vocals", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "no-vocals", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "instrumental", StringComparison.OrdinalIgnoreCase);
        });

        if (string.IsNullOrWhiteSpace(vocals) || string.IsNullOrWhiteSpace(instrumental))
        {
            string found = string.Join(", ", audioFiles.Select(path => Path.GetRelativePath(outputRoot, path)));
            throw new InvalidOperationException($"demucs did not produce expected stems; found: {found}");
        }

        string instrumentalFlac = await ConvertToFlacAsync(instrumental, Path.Combine(jobRoot, "instrumental.flac"), cancellationToken);
        string vocalsFlac = await ConvertToFlacAsync(vocals, Path.Combine(jobRoot, "vocals.flac"), cancellationToken);

        if (new FileInfo(instrumentalFlac).Length == new FileInfo(vocalsFlac).Length
            && await FilesAreEqualAsync(instrumentalFlac, vocalsFlac, cancellationToken))
        {
            throw new InvalidOperationException("demucs produced identical instrumental and vocals outputs.");
        }

        return (instrumentalFlac, vocalsFlac);
    }

    private async Task<string> ConvertToFlacAsync(string source, string destination, CancellationToken cancellationToken)
    {
        string tempDestination = destination + ".tmp.flac";
        if (File.Exists(tempDestination))
        {
            File.Delete(tempDestination);
        }

        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            source
        };

        if (_options.NormalizeLoudness)
        {
            arguments.Add("-af");
            arguments.Add(string.Format(
                CultureInfo.InvariantCulture,
                "loudnorm=I={0:0.###}:LRA={1:0.###}:TP={2:0.###}",
                _options.TargetIntegratedLufs,
                _options.TargetLoudnessRange,
                _options.TargetTruePeakDb));
        }

        arguments.AddRange(new[]
        {
            "-compression_level",
            "8",
            tempDestination
        });

        await RunProcessAsync(
            ResolveRequiredPath(_options.FfmpegPath, "ffmpeg"),
            arguments,
            Path.GetDirectoryName(destination) ?? Path.GetTempPath(),
            TimeSpan.FromSeconds(Math.Max(60, _options.ConversionTimeoutSeconds)),
            cancellationToken,
            ResolveFfmpegLocation(_options.FfmpegPath));

        File.Move(tempDestination, destination, overwrite: true);
        return destination;
    }

    private static async Task<bool> FilesAreEqualAsync(string first, string second, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 128;
        await using var firstStream = File.OpenRead(first);
        await using var secondStream = File.OpenRead(second);
        if (firstStream.Length != secondStream.Length)
        {
            return false;
        }

        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            int firstRead = await firstStream.ReadAsync(firstBuffer.AsMemory(0, bufferSize), cancellationToken);
            int secondRead = await secondStream.ReadAsync(secondBuffer.AsMemory(0, bufferSize), cancellationToken);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }

    private static string ResolveRequiredPath(string configuredPath, string fallback)
    {
        return string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath;
    }

    private static string ResolveFfmpegLocation(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.GetFileName(configuredPath).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(configuredPath).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(configuredPath) ?? configuredPath
            : configuredPath;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? extraPath = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (!string.IsNullOrWhiteSpace(extraPath))
        {
            startInfo.Environment["PATH"] = $"{extraPath};{startInfo.Environment["PATH"]}";
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                standardOutput.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                standardError.AppendLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start process: {fileName}");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to start process: {fileName}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Process timed out after {timeout.TotalSeconds:N0}s: {fileName}");
        }

        string stdout = standardOutput.ToString();
        string stderr = standardError.ToString();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process failed ({process.ExitCode}) {fileName}: {Truncate(stderr.Length > 0 ? stderr : stdout, 2000)}");
        }

        return new ProcessResult(stdout, stderr);
    }

    private static string Truncate(string value, int maxLength)
    {
        value = value.Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        int headLength = Math.Max(0, maxLength / 2);
        int tailLength = Math.Max(0, maxLength - headLength - 80);
        return value[..headLength]
            + $"{Environment.NewLine}... output truncated; preserving tail ...{Environment.NewLine}"
            + value[^tailLength..];
    }

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}
