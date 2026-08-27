using System.Diagnostics;
using System.Text.RegularExpressions;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public class YouTubeMetadataResolverService : IYouTubeMetadataResolverService
{
    private readonly WhisperRuntimeOptions _options;
    private readonly NetEaseLrcService _netEaseLrcService;
    private readonly ILogger<YouTubeMetadataResolverService> _logger;

    public YouTubeMetadataResolverService(
        IOptions<WhisperRuntimeOptions> options,
        NetEaseLrcService netEaseLrcService,
        ILogger<YouTubeMetadataResolverService> logger)
    {
        _options = options.Value;
        _netEaseLrcService = netEaseLrcService;
        _logger = logger;
    }

    public async Task<YouTubeMetadataResolutionResult> ResolveAsync(string youTubeUrl, string? title, string? artist, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedTitle = title ?? string.Empty;
        var resolvedArtist = artist ?? string.Empty;
        var normalizedYouTubeUrl = YouTubeVideoIdExtractor.NormalizeWatchUrl(youTubeUrl);
        if (normalizedYouTubeUrl is null)
        {
            _logger.LogWarning("拒絕無效的 YouTube 網址或影片 ID");
            if (!string.IsNullOrWhiteSpace(resolvedTitle) && !string.IsNullOrWhiteSpace(resolvedArtist))
            {
                resolvedTitle = StripArtistPrefixFromTitle(
                    NormalizeProvidedSongTitle(resolvedTitle),
                    resolvedArtist);
            }
            return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist);
        }

        if (!string.IsNullOrWhiteSpace(resolvedTitle) && !string.IsNullOrWhiteSpace(resolvedArtist))
        {
            var durationSeconds = await ResolveDurationSecondsAsync(normalizedYouTubeUrl, cancellationToken);
            resolvedTitle = StripArtistPrefixFromTitle(NormalizeProvidedSongTitle(resolvedTitle), resolvedArtist);
            return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist, durationSeconds);
        }

        var ytDlpPath = string.IsNullOrWhiteSpace(_options.YtDlpPath) ? "yt-dlp" : _options.YtDlpPath;

        try
        {
            using var ytProcess = new Process();
            ytProcess.StartInfo = CreateYtDlpStartInfo(
                ytDlpPath,
                "--print", "title",
                "--print", "artist",
                "--print", "creator",
                "--print", "uploader",
                "--print", "channel",
                "--print", "duration",
                "--", normalizedYouTubeUrl);
            ytProcess.Start();

            var outputTask = ytProcess.StandardOutput.ReadToEndAsync();
            var errorTask = ytProcess.StandardError.ReadToEndAsync();

            using var ytCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ytCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await ytProcess.WaitForExitAsync(ytCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(ytProcess);
                _logger.LogWarning("YouTube metadata resolution timed out after 30 seconds for {YouTubeUrl}", normalizedYouTubeUrl);
                return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(ytProcess);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (ytProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning("YouTube metadata resolution failed (exit {Code}): {Error}", ytProcess.ExitCode, error);
                return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist);
            }

            var lines = output.Split('\n', StringSplitOptions.TrimEntries);
            var rawTitle = lines.ElementAtOrDefault(0);
            var rawArtist = lines.ElementAtOrDefault(1);
            var rawCreator = lines.ElementAtOrDefault(2);
            var rawUploader = lines.ElementAtOrDefault(3);
            var rawChannel = lines.ElementAtOrDefault(4);
            var rawDuration = lines.ElementAtOrDefault(5);
            var durationSeconds = TryParseDurationSeconds(rawDuration);

            var normalized = TryNormalizeYouTubeMetadata(rawTitle, rawArtist, rawCreator, rawUploader, rawChannel);
            if (normalized.HasValue)
            {
                if (string.IsNullOrWhiteSpace(resolvedArtist) && !string.IsNullOrWhiteSpace(normalized.Value.Artist))
                {
                    resolvedArtist = normalized.Value.Artist;
                }

                if (string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    resolvedTitle = StripArtistPrefixFromTitle(normalized.Value.Title, resolvedArtist);
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedArtist) && !string.IsNullOrWhiteSpace(resolvedTitle))
            {
                resolvedArtist = await _netEaseLrcService.ResolvePrimaryArtistAsync(resolvedTitle) ?? string.Empty;
            }

            _logger.LogInformation(
                "YouTube metadata resolved. RawTitle={RawTitle}, RawArtist={RawArtist}, RawCreator={RawCreator}, RawUploader={RawUploader}, RawChannel={RawChannel}, RawDuration={RawDuration}; Title={Title}, Artist={Artist}, DurationSeconds={DurationSeconds}",
                rawTitle,
                rawArtist,
                rawCreator,
                rawUploader,
                rawChannel,
                rawDuration,
                resolvedTitle,
                resolvedArtist,
                durationSeconds);

            return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist, durationSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube metadata resolution failed for {YouTubeUrl}", normalizedYouTubeUrl);
            return new YouTubeMetadataResolutionResult(resolvedTitle, resolvedArtist);
        }
    }

    public async Task<double?> ResolveDurationSecondsAsync(string youTubeUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedYouTubeUrl = YouTubeVideoIdExtractor.NormalizeWatchUrl(youTubeUrl);
        if (normalizedYouTubeUrl is null)
        {
            return null;
        }

        var ytDlpPath = string.IsNullOrWhiteSpace(_options.YtDlpPath) ? "yt-dlp" : _options.YtDlpPath;

        try
        {
            using var ytProcess = new Process();
            ytProcess.StartInfo = CreateYtDlpStartInfo(
                ytDlpPath,
                "--print", "duration",
                "--", normalizedYouTubeUrl);
            ytProcess.Start();

            var outputTask = ytProcess.StandardOutput.ReadToEndAsync();
            var errorTask = ytProcess.StandardError.ReadToEndAsync();

            using var ytCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ytCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await ytProcess.WaitForExitAsync(ytCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(ytProcess);
                _logger.LogWarning("YouTube duration resolution timed out after 30 seconds for {YouTubeUrl}", normalizedYouTubeUrl);
                return null;
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(ytProcess);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;

            if (ytProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                _logger.LogWarning("YouTube duration resolution failed (exit {Code}): {Error}", ytProcess.ExitCode, error);
                return null;
            }

            return TryParseDurationSeconds(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube duration resolution failed for {YouTubeUrl}", normalizedYouTubeUrl);
            return null;
        }
    }

    private static ProcessStartInfo CreateYtDlpStartInfo(string ytDlpPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static double? TryParseDurationSeconds(string? rawDuration)
    {
        if (string.IsNullOrWhiteSpace(rawDuration))
        {
            return null;
        }

        return double.TryParse(
            rawDuration.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var durationSeconds)
            ? durationSeconds
            : null;
    }

    private static void TryKillProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore best-effort kill failures
        }
    }

    public static (string Title, string? Artist)? TryNormalizeYouTubeMetadata(string? rawTitle, params string?[] artistCandidates)
    {
        var title = NormalizeYouTubeSongTitle(rawTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artist = artistCandidates
            .Select(NormalizeYouTubeArtist)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

        title = StripArtistPrefixFromTitle(title, artist);

        return (title, artist);
    }

    public static string NormalizeYouTubeSongTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        var title = rawTitle.Trim();

        var quotedJapanese = Regex.Match(title, "[「『](?<title>[^」』]+)[」』]");
        if (quotedJapanese.Success)
        {
            title = quotedJapanese.Groups["title"].Value;
        }
        else
        {
            title = Regex.Replace(title, @"\s*/\s*.*$", string.Empty);
        }

        title = title.Replace("（", "(").Replace("）", ")");
        title = Regex.Replace(title, @"^\s*[【［\[][^】］\]]+[】］\]]\s*", string.Empty);
        title = Regex.Replace(title, @"\([^)]*(?:MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video|VIDEO)[^)]*\)", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\[[^\]]*(?:MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video|VIDEO)[^\]]*\]", string.Empty, RegexOptions.IgnoreCase);
        title = StripDanglingBracketSuffix(title);
        title = Regex.Replace(title, @"\s*(MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video).*", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\([^)]*\)", string.Empty);
        title = Regex.Replace(title, @"\s+", " ").Trim();
        title = title.Trim('"', '“', '”', '『', '』', '「', '」', '【', '】', '［', '］');
        return title.Trim();
    }

    public static string NormalizeProvidedSongTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        var title = rawTitle.Trim().Replace("（", "(").Replace("）", ")");
        title = Regex.Replace(title, @"^\s*[【［\[][^】］\]]+[】］\]]\s*", string.Empty);
        title = Regex.Replace(title, @"\([^)]*(?:MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video|VIDEO)[^)]*\)", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\[[^\]]*(?:MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video|VIDEO)[^\]]*\]", string.Empty, RegexOptions.IgnoreCase);
        title = StripDanglingBracketSuffix(title);
        title = Regex.Replace(title, @"\s*(MUSIC\s*VIDEO|MV|Official\s*Video|Official|HDver\.?|HD\s*ver\.?|Lyric\s*Video)\s*$", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+", " ").Trim();
        title = title.Trim('"', '“', '”', '『', '』', '「', '」', '【', '】', '［', '］');
        return title.Trim();
    }

    public static string StripArtistPrefixFromTitle(string? rawTitle, string? rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(rawArtist))
        {
            return rawTitle?.Trim() ?? string.Empty;
        }

        var title = rawTitle.Trim();
        var artistAliases = BuildArtistAliases(rawArtist);
        foreach (var alias in artistAliases)
        {
            var escapedAlias = Regex.Escape(alias);
            var match = Regex.Match(title, $@"^\s*{escapedAlias}\s*(?:[-–—ー]|:|：)\s*(?<title>.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var stripped = match.Groups["title"].Value.Trim();
                return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
            }
        }

        return title;
    }

    private static IReadOnlyList<string> BuildArtistAliases(string rawArtist)
    {
        var aliases = new List<string> { rawArtist.Trim() };
        aliases.AddRange(Regex.Split(rawArtist, @"\s*(?:/|／|,|、|&|＆|feat\.?|featuring)\s*", RegexOptions.IgnoreCase)
            .SelectMany(part => new[]
            {
                part.Trim(),
                Regex.Replace(part, @"\s*[（(][^）)]*[）)]\s*", string.Empty).Trim()
            })
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        return aliases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(alias => alias.Length)
            .ToArray();
    }

    private static string StripDanglingBracketSuffix(string title)
    {
        int? cutIndex = null;

        foreach (var (open, close) in new[]
                 {
                     ('【', '】'),
                     ('［', '］'),
                     ('[', ']'),
                     ('（', '）'),
                     ('(', ')')
                 })
        {
            var unmatchedOpens = new Stack<int>();
            for (var i = 0; i < title.Length; i++)
            {
                if (title[i] == open)
                {
                    unmatchedOpens.Push(i);
                    continue;
                }

                if (title[i] == close && unmatchedOpens.Count > 0)
                {
                    unmatchedOpens.Pop();
                }
            }

            if (unmatchedOpens.Count == 0)
            {
                continue;
            }

            var earliestUnmatchedOpen = unmatchedOpens.Min();
            if (earliestUnmatchedOpen <= 0)
            {
                continue;
            }

            cutIndex = !cutIndex.HasValue || earliestUnmatchedOpen < cutIndex.Value
                ? earliestUnmatchedOpen
                : cutIndex;
        }

        return cutIndex.HasValue
            ? title[..cutIndex.Value].Trim()
            : title;
    }

    public static string? NormalizeYouTubeArtist(string? rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return null;
        }

        var artist = rawArtist.Trim();
        if (string.Equals(artist, "NA", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        artist = Regex.Replace(artist, @"\s*-\s*Topic$", string.Empty, RegexOptions.IgnoreCase);
        artist = Regex.Replace(artist, @"\s+Official.*$", string.Empty, RegexOptions.IgnoreCase);
        artist = Regex.Replace(artist, @"\s+", " ").Trim();

        if (Regex.IsMatch(artist, @"\b(records?|music|official|channel|entertainment|animation)\b", RegexOptions.IgnoreCase))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(artist) ? null : artist;
    }
}
