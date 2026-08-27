using Microsoft.AspNetCore.WebUtilities;

namespace LearnMore.Services;

public static class YouTubeVideoIdExtractor
{
    public static string? Extract(string? urlOrVideoId)
    {
        if (string.IsNullOrWhiteSpace(urlOrVideoId))
        {
            return null;
        }

        string value = urlOrVideoId.Trim();
        if (NormalizeVideoId(value) is { } rawVideoId)
        {
            return rawVideoId;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        string host = uri.Host.ToLowerInvariant();
        bool isYoutubeHost = host == "youtube.com"
                             || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                             || host == "youtube-nocookie.com"
                             || host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase);
        bool isShortYoutubeHost = host is "youtu.be" or "www.youtu.be";
        if (!isYoutubeHost && !isShortYoutubeHost)
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("v", out var videoId))
        {
            return NormalizeVideoId(videoId.ToString());
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (isShortYoutubeHost && segments.Length >= 1)
        {
            return NormalizeVideoId(segments[0]);
        }

        if (isYoutubeHost && segments.Length >= 2 && segments[0] is "embed" or "shorts" or "live")
        {
            return NormalizeVideoId(segments[1]);
        }

        return null;
    }

    public static string? NormalizeWatchUrl(string? urlOrVideoId)
    {
        var videoId = Extract(urlOrVideoId);
        return videoId is null
            ? null
            : $"https://www.youtube.com/watch?v={videoId}";
    }

    private static string? NormalizeVideoId(string? videoId)
    {
        videoId = videoId?.Trim();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        int queryIndex = videoId.IndexOfAny(['?', '&', '#']);
        if (queryIndex >= 0)
        {
            videoId = videoId[..queryIndex];
        }

        return videoId.Length == 11 && videoId.All(IsYouTubeVideoIdChar) ? videoId : null;
    }

    private static bool IsYouTubeVideoIdChar(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '_' or '-';
    }
}
