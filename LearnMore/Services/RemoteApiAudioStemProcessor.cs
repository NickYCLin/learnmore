using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public sealed class RemoteApiAudioStemProcessor : IAudioStemProcessor
{
    private static readonly Regex SafeSongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly AudioStemProcessingOptions _options;
    private readonly IAudioStemJobService _jobService;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DemucsAudioStemProcessor _localProcessor;
    private readonly ILogger<RemoteApiAudioStemProcessor> _logger;

    public RemoteApiAudioStemProcessor(
        IOptions<AudioStemProcessingOptions> options,
        IAudioStemJobService jobService,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        DemucsAudioStemProcessor localProcessor,
        ILogger<RemoteApiAudioStemProcessor> logger)
    {
        _options = options.Value;
        _jobService = jobService;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _localProcessor = localProcessor;
        _logger = logger;
    }

    public async Task ProcessAsync(AudioStemJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            await ProcessRemoteAsync(job, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _options.RemoteApiFallbackToLocal)
        {
            _logger.LogWarning(ex, "遠端音軌分離失敗，改用本機處理 songUid={SongUid}", job.SongUid);
            await _localProcessor.ProcessAsync(job, cancellationToken);
        }
    }

    private async Task ProcessRemoteAsync(AudioStemJob job, CancellationToken cancellationToken)
    {
        if (!SafeSongUidPattern.IsMatch(job.SongUid))
        {
            throw new InvalidOperationException("Invalid SongUid for audio stem processing.");
        }

        if (string.IsNullOrWhiteSpace(_options.RemoteApiBaseUrl))
        {
            throw new InvalidOperationException("AudioStemProcessing:RemoteApiBaseUrl is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.RemoteApiToken))
        {
            throw new InvalidOperationException("AudioStemProcessing:RemoteApiToken is not configured.");
        }

        string workRoot = string.IsNullOrWhiteSpace(_options.WorkRoot)
            ? Path.Combine(Path.GetTempPath(), "learnmore-audio-stems")
            : _options.WorkRoot;
        string jobRoot = Path.Combine(workRoot, "remote-api", job.SongUid);
        if (Directory.Exists(jobRoot))
        {
            Directory.Delete(jobRoot, recursive: true);
        }
        Directory.CreateDirectory(jobRoot);

        try
        {
            var response = await SeparateWithRemoteApiAsync(job, cancellationToken);
            var instrumentalStem = FindStem(response.Stems, "instrumental");
            var vocalsStem = FindStem(response.Stems, "vocals");

            string instrumentalSource = await DownloadStemAsync(instrumentalStem, jobRoot, "instrumental-source", cancellationToken);
            string vocalsSource = await DownloadStemAsync(vocalsStem, jobRoot, "vocals-source", cancellationToken);
            string instrumentalFlac = await ConvertToFlacAsync(instrumentalSource, Path.Combine(jobRoot, "instrumental.flac"), cancellationToken);
            string vocalsFlac = await ConvertToFlacAsync(vocalsSource, Path.Combine(jobRoot, "vocals.flac"), cancellationToken);

            if (new FileInfo(instrumentalFlac).Length == new FileInfo(vocalsFlac).Length
                && await FilesAreEqualAsync(instrumentalFlac, vocalsFlac, cancellationToken))
            {
                throw new InvalidOperationException("Remote separation produced identical instrumental and vocals outputs.");
            }

            string destinationRoot = Path.Combine(_environment.WebRootPath, "audio-stems", job.SongUid);
            Directory.CreateDirectory(destinationRoot);
            string instrumentalDestination = Path.Combine(destinationRoot, "instrumental.flac");
            string vocalsDestination = Path.Combine(destinationRoot, "vocals.flac");
            File.Copy(instrumentalFlac, instrumentalDestination, overwrite: true);
            File.Copy(vocalsFlac, vocalsDestination, overwrite: true);

            await _jobService.RegisterCompletedStemsAsync(
                job.SongUid,
                instrumentalDestination,
                vocalsDestination,
                response.Model,
                "remote-demucs",
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
                _logger.LogDebug(ex, "清理遠端音軌處理暫存資料夾失敗 {JobRoot}", jobRoot);
            }
        }
    }

    private async Task<SeparateYouTubeResponse> SeparateWithRemoteApiAsync(AudioStemJob job, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _options.RemoteApiTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRemoteUri("v1/separate-youtube"))
        {
            Content = JsonContent.Create(new SeparateYouTubeRequest(job.SongUid, job.YouTubeVideoUrl, _options.ModelName))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.RemoteApiToken);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, timeoutCts.Token);
        string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Remote audio separation failed ({(int)response.StatusCode}): {Truncate(body, 1000)}");
        }

        var result = System.Text.Json.JsonSerializer.Deserialize<SeparateYouTubeResponse>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        if (result == null)
        {
            throw new InvalidOperationException("Remote audio separation returned an empty response.");
        }

        return result;
    }

    private async Task<string> DownloadStemAsync(AudioStem stem, string jobRoot, string destinationName, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(stem.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".audio";
        }

        string destination = Path.Combine(jobRoot, destinationName + extension);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _options.RemoteApiDownloadTimeoutSeconds)));

        string relativePath = !string.IsNullOrWhiteSpace(stem.DownloadPath)
            ? stem.DownloadPath
            : "v1/audio-stem-file?path=" + Uri.EscapeDataString(stem.Path);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRemoteUri(relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.RemoteApiToken);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            throw new InvalidOperationException($"Remote audio stem download failed ({(int)response.StatusCode}): {Truncate(body, 1000)}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, timeoutCts.Token);
        return destination;
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

    private Uri BuildRemoteUri(string relativePath)
    {
        string baseUrl = _options.RemoteApiBaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), relativePath);
    }

    private static AudioStem FindStem(IEnumerable<AudioStem> stems, string kind)
    {
        AudioStem? stem = stems.FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (stem == null)
        {
            throw new InvalidOperationException($"Remote audio separation did not return {kind} stem.");
        }

        return stem;
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

    private static async Task RunProcessAsync(
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
            startInfo.Environment["PATH"] = $"{extraPath}{Path.PathSeparator}{startInfo.Environment["PATH"]}";
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

    private sealed record SeparateYouTubeRequest(
        [property: JsonPropertyName("songUid")] string SongUid,
        [property: JsonPropertyName("youtubeUrl")] string YouTubeUrl,
        [property: JsonPropertyName("model")] string Model);

    private sealed record SeparateYouTubeResponse(
        [property: JsonPropertyName("songUid")] string? SongUid,
        [property: JsonPropertyName("youtubeUrl")] string YouTubeUrl,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("outputDir")] string OutputDir,
        [property: JsonPropertyName("stems")] List<AudioStem> Stems);

    private sealed record AudioStem(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("fileName")] string FileName,
        [property: JsonPropertyName("sizeBytes")] long SizeBytes,
        [property: JsonPropertyName("downloadPath")] string? DownloadPath);
}
