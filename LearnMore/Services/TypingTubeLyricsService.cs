using LearnMore.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LearnMore.Services;

/// <summary>
/// 從 TypingTube 取得人工製作的歌詞時間軸。
/// 只作為 LrcLib / NetEase 都找不到時的保守 fallback；候選頁必須能用 YouTube video id 驗證。
/// </summary>
public sealed partial class TypingTubeLyricsService
{
    private const string BaseUrl = "https://typing-tube.net";
    private readonly ILogger<TypingTubeLyricsService> _logger;

    public TypingTubeLyricsService(ILogger<TypingTubeLyricsService> logger)
    {
        _logger = logger;
    }

    public async Task<List<LyricSegment>?> FetchLyricsByYouTubeUrlAsync(string youTubeUrl, string? title, CancellationToken cancellationToken = default)
    {
        var videoId = ExtractYouTubeVideoId(youTubeUrl);
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        try
        {
            var movieIds = await SearchMovieIdsAsync(videoId, title, cancellationToken);
            foreach (var movieId in movieIds.Distinct().Take(5))
            {
                var result = await TryFetchMovieLyricsAsync(movieId, videoId, cancellationToken);
                if (result is { Count: >= 3 })
                {
                    _logger.LogInformation("TypingTube: movieId={MovieId} 取得 {Count} 行人工時間軸", movieId, result.Count);
                    return result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypingTube fallback failed for {YouTubeUrl}", youTubeUrl);
        }

        return null;
    }

    private static string? ExtractYouTubeVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var watch = Regex.Match(url, @"[?&]v=([A-Za-z0-9_-]{6,})");
        if (watch.Success) return watch.Groups[1].Value;

        var shortUrl = Regex.Match(url, @"youtu\.be/([A-Za-z0-9_-]{6,})");
        if (shortUrl.Success) return shortUrl.Groups[1].Value;

        var embed = Regex.Match(url, @"youtube\.com/(?:embed|shorts)/([A-Za-z0-9_-]{6,})");
        return embed.Success ? embed.Groups[1].Value : null;
    }

    private async Task<List<int>> SearchMovieIdsAsync(string videoId, string? title, CancellationToken cancellationToken)
    {
        var queries = new List<string>
        {
            $"site:typing-tube.net/movie/show {videoId}"
        };

        if (!string.IsNullOrWhiteSpace(title))
            queries.Add($"site:typing-tube.net/movie/show \"{title.Trim()}\"");

        var ids = new List<int>();
        using var client = CreateHttpClient();
        foreach (var query in queries)
        {
            var url = "https://duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                continue;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            ids.AddRange(ExtractMovieIdsFromSearchHtml(html));
        }

        return ids;
    }

    public static List<int> ExtractMovieIdsFromSearchHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<int>();

        var decoded = WebUtility.UrlDecode(WebUtility.HtmlDecode(html));
        var ids = new List<int>();
        foreach (Match match in Regex.Matches(decoded, @"typing-tube\.net/movie/show/(\d+)|/movie/show/(\d+)"))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (int.TryParse(value, out var id))
                ids.Add(id);
        }

        return ids.Distinct().ToList();
    }

    private async Task<List<LyricSegment>?> TryFetchMovieLyricsAsync(int movieId, string expectedVideoId, CancellationToken cancellationToken)
    {
        var cookieContainer = new CookieContainer();
        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = cookieContainer, AutomaticDecompression = DecompressionMethods.All };
        using var client = CreateHttpClient(handler);

        var page = await client.GetStringAsync($"{BaseUrl}/movie/show/{movieId}", cancellationToken);
        if (!PageMatchesVideoId(page, expectedVideoId))
            return null;

        var csrfToken = ExtractMetaContent(page, "csrf-token");
        var gameToken = ExtractMetaContent(page, "game-token");
        var lyricsKey = ExtractMetaContent(page, "lyrics-key");
        if (string.IsNullOrWhiteSpace(csrfToken) || string.IsNullOrWhiteSpace(gameToken) || string.IsNullOrWhiteSpace(lyricsKey))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/lyrics/{movieId}?token={Uri.EscapeDataString(gameToken)}");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrfToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("TypingTube: movieId={MovieId} lyrics API HTTP {StatusCode}", movieId, (int)response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var tsv = DecryptLyricsPayload(json, lyricsKey);
        return ParseLyricsTsv(tsv);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; LearnMore/1.0; +https://magicplus-design.serveirc.com)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
        return client;
    }

    private static bool PageMatchesVideoId(string page, string videoId)
    {
        return page.Contains($"youtu.be/{videoId}", StringComparison.OrdinalIgnoreCase)
            || page.Contains($"youtube.com/watch?v={videoId}", StringComparison.OrdinalIgnoreCase)
            || page.Contains($"img.youtube.com/vi/{videoId}/", StringComparison.OrdinalIgnoreCase)
            || page.Contains($"youtube.com/embed/{videoId}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractMetaContent(string html, string name)
    {
        var match = Regex.Match(html, $"<meta\\s+name=\\\"{Regex.Escape(name)}\\\"\\s+content=\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string DecryptLyricsPayload(string json, string base64Key)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var encrypted = Convert.FromBase64String(root.GetProperty("encrypted").GetString() ?? string.Empty);
        var iv = Convert.FromBase64String(root.GetProperty("iv").GetString() ?? string.Empty);
        var tag = Convert.FromBase64String(root.GetProperty("auth_tag").GetString() ?? string.Empty);
        var key = Convert.FromBase64String(base64Key);

        var plain = new byte[encrypted.Length];
        using var aes = new AesGcm(key, tagSizeInBytes: tag.Length);
        aes.Decrypt(iv, encrypted, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static List<LyricSegment> ParseLyricsTsv(string tsv)
    {
        var result = new List<LyricSegment>();
        if (string.IsNullOrWhiteSpace(tsv)) return result;

        foreach (var rawLine in tsv.Split('\n').Skip(1))
        {
            var columns = rawLine.TrimEnd('\r').Split('\t');
            if (columns.Length < 2) continue;
            if (!double.TryParse(columns[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var timeStamp))
                continue;

            var japanese = CleanLyricText(columns[1]);
            if (string.IsNullOrWhiteSpace(japanese) || string.Equals(japanese, "end", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new LyricSegment
            {
                TimeStamp = timeStamp,
                Japanese = japanese,
                Chinese = string.Empty,
            });
        }

        return result;
    }

    private static string CleanLyricText(string text)
    {
        var withoutRt = Regex.Replace(text, "<rt>.*?</rt>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        withoutRt = Regex.Replace(withoutRt, "</?ruby>", string.Empty, RegexOptions.IgnoreCase);
        withoutRt = Regex.Replace(withoutRt, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutRt).Trim();
    }
}
