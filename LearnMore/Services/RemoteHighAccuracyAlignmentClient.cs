using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public sealed class RemoteHighAccuracyAlignmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly VocalOnsetDetectionOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RemoteHighAccuracyAlignmentClient> _logger;

    public RemoteHighAccuracyAlignmentClient(
        IOptions<VocalOnsetDetectionOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<RemoteHighAccuracyAlignmentClient> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.UseRemoteHighAccuracyApi
        && !string.IsNullOrWhiteSpace(_options.RemoteHighAccuracyApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.RemoteHighAccuracyApiToken);

    public async Task<VocalOnsetDetectionService.AlignmentAttemptResult> AlignAsync(
        SongLyricsProcessingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new VocalOnsetDetectionService.AlignmentAttemptResult(
                false,
                new List<VocalOnsetDetectionService.LyricTimingAlignment>(),
                "remote_high_accuracy_api_not_configured");
        }

        var requestBody = new HighAccuracyAlignRequest(
            snapshot.SongUid,
            snapshot.YouTubeUrl,
            "ja",
            snapshot.Lyrics
                .Where(lyric => !string.IsNullOrWhiteSpace(lyric.Japanese))
                .Select(lyric => new HighAccuracyLyricLine(lyric.LyricID, lyric.Japanese, lyric.TimeStamp))
                .ToList());

        if (requestBody.Lyrics.Count == 0)
        {
            return new VocalOnsetDetectionService.AlignmentAttemptResult(
                false,
                new List<VocalOnsetDetectionService.LyricTimingAlignment>(),
                "remote_high_accuracy_lyrics_empty");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _options.RemoteHighAccuracyApiTimeoutSeconds)));

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRemoteUri("v1/high-accuracy-align"))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.RemoteHighAccuracyApiToken);

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return new VocalOnsetDetectionService.AlignmentAttemptResult(
                false,
                new List<VocalOnsetDetectionService.LyricTimingAlignment>(),
                "remote_high_accuracy_api_failed",
                $"HTTP {(int)response.StatusCode}: {Truncate(body, 1000)}");
        }

        var result = JsonSerializer.Deserialize<HighAccuracyAlignResponse>(body, JsonOptions);
        if (result == null)
        {
            return new VocalOnsetDetectionService.AlignmentAttemptResult(
                false,
                new List<VocalOnsetDetectionService.LyricTimingAlignment>(),
                "remote_high_accuracy_api_empty_response");
        }

        var linesById = result.AlignedLines.ToDictionary(line => line.LyricId);
        var alignments = new List<VocalOnsetDetectionService.LyricTimingAlignment>(snapshot.Lyrics.Count);
        foreach (var lyric in snapshot.Lyrics)
        {
            if (!linesById.TryGetValue(lyric.LyricID, out var line))
            {
                alignments.Add(new VocalOnsetDetectionService.LyricTimingAlignment(
                    lyric.Japanese,
                    lyric.TimeStamp,
                    lyric.TimeStamp,
                    lyric.TimeStamp,
                    0,
                    false,
                    -1,
                    -1));
                continue;
            }

            var isMatched = IsReliableRemoteMatch(line);
            alignments.Add(new VocalOnsetDetectionService.LyricTimingAlignment(
                lyric.Japanese,
                lyric.TimeStamp,
                line.Start,
                Math.Max(line.End, line.Start + 0.25),
                line.Score,
                isMatched,
                line.WhisperWordStartIndex ?? -1,
                line.WhisperWordEndIndex ?? -1));
        }

        _logger.LogInformation(
            "LearnMoreAPI 高精度校準完成 songUid={SongUid}, model={Model}, matched={Matched}/{Total}",
            snapshot.SongUid,
            result.Model,
            result.MatchedCount,
            result.TotalCount);

        return new VocalOnsetDetectionService.AlignmentAttemptResult(
            alignments.Count == snapshot.Lyrics.Count,
            alignments,
            WordCount: 0,
            MatchedCount: alignments.Count(alignment => alignment.IsMatched),
            CorrectionStrategy: "learnmore_api_whisper");
    }

    private Uri BuildRemoteUri(string relativePath)
    {
        var baseUrl = _options.RemoteHighAccuracyApiBaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), relativePath);
    }

    private static string Truncate(string value, int maxLength)
    {
        value = value.Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static bool IsReliableRemoteMatch(AlignedLyricLine line)
    {
        var source = line.Source ?? string.Empty;
        if (string.Equals(source, "proportional_fallback", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "current_timestamp_context", StringComparison.OrdinalIgnoreCase)
            || source.Contains("unverified", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (source.StartsWith("shazam_timed_lyrics_verified", StringComparison.OrdinalIgnoreCase))
        {
            return line.Score >= 0.52;
        }

        if (IsLatinDominantLyric(line.Japanese) && !IsReliableLatinAlignmentSource(source))
        {
            return false;
        }

        return source switch
        {
            "whisper_word_match" or "whisperx_word_match" => line.Score >= 0.70,
            "whisper_segment_match" or "whisperx_segment_match" => line.Score >= 0.58,
            "whisper_timing_hint_match" or "whisperx_timing_hint_match" => line.Score >= 0.72,
            "whisper_global_sequence_match" or "whisperx_global_sequence_match" => line.Score >= 0.60,
            "whisper_local_asr_anchor_match" or "whisperx_local_asr_anchor_match" => line.Score >= 0.70,
            "whisper_lyric_forced_alignment_global_context" or "whisperx_lyric_forced_alignment_global_context" => line.Score >= 0.60,
            "whisper_contextual_timestamp_bridge" or "whisperx_contextual_timestamp_bridge" => line.Score >= 0.85,
            "whisper_lyric_forced_alignment_asr_verified" or "whisperx_lyric_forced_alignment_asr_verified" => line.Score >= 0.70,
            "whisper_lyric_forced_alignment_internal" or "whisperx_lyric_forced_alignment_internal" => line.Score >= 0.90,
            "whisper_lyric_forced_alignment_sequence" or "whisperx_lyric_forced_alignment_sequence" => line.Score >= 0.90,
            "whisper_lyric_forced_alignment_sequence_bridge" or "whisperx_lyric_forced_alignment_sequence_bridge" => line.Score >= 0.90,
            "whisper_lyric_forced_alignment" or "whisperx_lyric_forced_alignment" => false,
            _ => line.Score >= 0.80,
        };
    }

    private static bool IsLatinDominantLyric(string text)
    {
        var latinCount = Regex.Matches(text, "[A-Za-z]").Count;
        if (latinCount < 4)
        {
            return false;
        }

        var japaneseCount = Regex.Matches(text, @"[\u3040-\u30ff\u3400-\u9fff]").Count;
        if (japaneseCount == 0)
        {
            return true;
        }

        return (double)latinCount / Math.Max(latinCount + japaneseCount, 1) >= 0.45;
    }

    private static bool IsReliableLatinAlignmentSource(string source)
    {
        if (source.StartsWith("shazam_timed_lyrics_verified", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return source switch
        {
            "whisper_word_match" or "whisperx_word_match" => true,
            "whisper_local_asr_anchor_match" or "whisperx_local_asr_anchor_match" => true,
            "whisper_lyric_forced_alignment_asr_verified" or "whisperx_lyric_forced_alignment_asr_verified" => true,
            _ => false
        };
    }

    private sealed record HighAccuracyAlignRequest(
        [property: JsonPropertyName("songUid")] string SongUid,
        [property: JsonPropertyName("youtubeUrl")] string YoutubeUrl,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("lyrics")] List<HighAccuracyLyricLine> Lyrics);

    private sealed record HighAccuracyLyricLine(
        [property: JsonPropertyName("lyricId")] int LyricId,
        [property: JsonPropertyName("japanese")] string Japanese,
        [property: JsonPropertyName("currentStart")] double CurrentStart);

    private sealed record HighAccuracyAlignResponse(
        [property: JsonPropertyName("songUid")] string? SongUid,
        [property: JsonPropertyName("youtubeUrl")] string YoutubeUrl,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("durationSeconds")] double? DurationSeconds,
        [property: JsonPropertyName("alignedLines")] List<AlignedLyricLine> AlignedLines,
        [property: JsonPropertyName("matchedCount")] int MatchedCount,
        [property: JsonPropertyName("totalCount")] int TotalCount);

    private sealed record AlignedLyricLine(
        [property: JsonPropertyName("lyricId")] int LyricId,
        [property: JsonPropertyName("japanese")] string Japanese,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("whisperSegmentIndex")] int? WhisperSegmentIndex,
        [property: JsonPropertyName("whisperWordStartIndex")] int? WhisperWordStartIndex,
        [property: JsonPropertyName("whisperWordEndIndex")] int? WhisperWordEndIndex);
}
