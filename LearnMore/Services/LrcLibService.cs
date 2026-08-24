using System.Text.RegularExpressions;

namespace LearnMore.Services
{
    /// <summary>
    /// 從 lrclib.net 搜尋並解析 synced LRC 歌詞（帶精準時間戳）。
    /// API 完全免費、不需要 key。
    /// </summary>
    public class LrcLibService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<LrcLibService> _logger;

        public LrcLibService(IHttpClientFactory httpClientFactory, ILogger<LrcLibService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// 搜尋歌曲並回傳精準時間戳歌詞行。
        /// </summary>
        public async Task<List<(double TimeStamp, string Japanese)>?> FetchSyncedLyricsAsync(
            string title, string artist, string? album = null, double? durationSeconds = null)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LearnMore/1.0 (https://magicplus-design.serveirc.com)");
                client.Timeout = TimeSpan.FromSeconds(10);

                // 一律用搜尋模式取日文字元最多的版本（最準確），但候選必須符合曲名/歌手。
                foreach (var titleCandidate in SyncedLyricsMetadataMatcher.BuildTitleCandidates(title))
                {
                    var result = await TrySearch(client, titleCandidate, artist, title, durationSeconds);
                    if (result != null && result.Count > 0)
                        return result;
                }

                if (string.IsNullOrWhiteSpace(artist))
                {
                    foreach (var titleCandidate in SyncedLyricsMetadataMatcher.BuildTitleCandidates(title))
                    {
                        var result = await TrySearch(client, titleCandidate, null, title, durationSeconds);
                        if (result != null && result.Count > 0)
                            return result;
                    }
                }

                // Fallback：直接 get（不帶 album），仍使用清理過的曲名避免括號副標題造成錯配。
                foreach (var titleCandidate in SyncedLyricsMetadataMatcher.BuildTitleCandidates(title))
                {
                    var result = await TryFetch(client, titleCandidate, artist, null);
                    if (result != null && result.Count > 0)
                        return result;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LrcLib FetchSyncedLyricsAsync failed for {Title} {Artist}", title, artist);
                return null;
            }
        }

        private async Task<List<(double TimeStamp, string Japanese)>?> TryFetch(
            HttpClient client, string title, string? artist, string? album)
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(artist))
                url += $"&artist_name={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrEmpty(album))
                url += $"&album_name={Uri.EscapeDataString(album)}";

            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            return ParseLrcFromJson(json);
        }

        private async Task<List<(double TimeStamp, string Japanese)>?> TrySearch(
            HttpClient client, string title, string? artist, string requestedTitle, double? durationSeconds)
        {
            // 使用 track_name + artist_name 參數搜尋，比 ?q= 關鍵字搜尋更精準
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(artist))
                url += $"&artist_name={Uri.EscapeDataString(artist)}";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(json);
            return arr == null ? null : SelectBestSyncedLyricsCandidate(arr, requestedTitle, artist, durationSeconds);
        }

        public static List<(double TimeStamp, string Japanese)>? SelectBestSyncedLyricsCandidate(
            IReadOnlyList<System.Text.Json.JsonElement> candidates)
            => SelectBestSyncedLyricsCandidate(candidates, requestedTitle: null, requestedArtist: null, durationSeconds: null);

        public static List<(double TimeStamp, string Japanese)>? SelectBestSyncedLyricsCandidate(
            IReadOnlyList<System.Text.Json.JsonElement> candidates,
            string? requestedTitle,
            string? requestedArtist,
            double? durationSeconds = null)
        {
            CandidateScore? best = null;

            foreach (var item in candidates)
            {
                if (!IsMetadataMatch(item, requestedTitle, requestedArtist))
                    continue;

                if (!item.TryGetProperty("syncedLyrics", out var syncedProp) ||
                    syncedProp.ValueKind == System.Text.Json.JsonValueKind.Null)
                    continue;

                var synced = syncedProp.GetString();
                if (string.IsNullOrWhiteSpace(synced))
                    continue;

                var lines = ParseLrc(synced);
                if (lines.Count < 5)
                    continue;

                var candidateDuration = TryGetDurationSeconds(item);
                var score = ScoreCandidate(lines, durationSeconds, candidateDuration);
                if (best == null || score.CompareTo(best.Value) > 0)
                    best = score;
            }

            return best?.Lines;
        }

        private static bool IsMetadataMatch(
            System.Text.Json.JsonElement item,
            string? requestedTitle,
            string? requestedArtist)
        {
            if (string.IsNullOrWhiteSpace(requestedTitle) && string.IsNullOrWhiteSpace(requestedArtist))
            {
                return true;
            }

            var trackName = item.TryGetProperty("trackName", out var trackNameProp)
                ? trackNameProp.GetString()
                : null;
            var artistName = item.TryGetProperty("artistName", out var artistNameProp)
                ? artistNameProp.GetString()
                : null;

            return SyncedLyricsMetadataMatcher.IsLikelyTrackMatch(trackName, requestedTitle ?? string.Empty)
                && SyncedLyricsMetadataMatcher.IsLikelyArtistMatch(artistName, requestedArtist ?? string.Empty);
        }

        private List<(double TimeStamp, string Japanese)>? ParseLrcFromJson(string json)
        {
            try
            {
                var obj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                if (!obj.TryGetProperty("syncedLyrics", out var syncedProp) ||
                    syncedProp.ValueKind == System.Text.Json.JsonValueKind.Null)
                    return null;

                var synced = syncedProp.GetString();
                if (string.IsNullOrWhiteSpace(synced)) return null;

                return ParseLrc(synced);
            }
            catch { return null; }
        }

        private static double? TryGetDurationSeconds(System.Text.Json.JsonElement item)
        {
            if (!item.TryGetProperty("duration", out var durationProp))
                return null;

            return durationProp.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when durationProp.TryGetDouble(out var value) => value,
                System.Text.Json.JsonValueKind.String when double.TryParse(
                    durationProp.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value) => value,
                _ => null
            };
        }

        private static CandidateScore ScoreCandidate(
            List<(double TimeStamp, string Japanese)> lines,
            double? requestedDurationSeconds,
            double? candidateDurationSeconds)
        {
            int jpChars = lines.Sum(l => l.Japanese.Count(c =>
                (c >= '\u3040' && c <= '\u309F') ||
                (c >= '\u30A0' && c <= '\u30FF') ||
                (c >= '\u4E00' && c <= '\u9FFF')));

            var firstTexts = string.Join("|", lines.Take(4).Select(l => l.Japanese));
            var firstTimestamp = lines[0].TimeStamp;
            var durationDelta = requestedDurationSeconds.HasValue && candidateDurationSeconds.HasValue
                ? Math.Abs(candidateDurationSeconds.Value - requestedDurationSeconds.Value)
                : double.PositiveInfinity;
            return new CandidateScore(lines, durationDelta, jpChars, lines.Count, firstTexts, firstTimestamp);
        }

        private readonly record struct CandidateScore(
            List<(double TimeStamp, string Japanese)> Lines,
            double DurationDelta,
            int JpChars,
            int LineCount,
            string FirstTexts,
            double FirstTimestamp) : IComparable<CandidateScore>
        {
            public int CompareTo(CandidateScore other)
            {
                if (!double.IsPositiveInfinity(DurationDelta) || !double.IsPositiveInfinity(other.DurationDelta))
                {
                    var duration = other.DurationDelta.CompareTo(DurationDelta);
                    if (duration != 0) return duration;
                }

                var jp = JpChars.CompareTo(other.JpChars);
                if (jp != 0) return jp;

                var lineCount = LineCount.CompareTo(other.LineCount);
                if (lineCount != 0) return lineCount;

                var sameShape = string.Equals(FirstTexts, other.FirstTexts, StringComparison.Ordinal);
                if (sameShape)
                {
                    var firstTs = FirstTimestamp.CompareTo(other.FirstTimestamp);
                    if (firstTs != 0) return firstTs;
                }

                return 0;
            }
        }

        /// <summary>
        /// 解析 LRC 格式：[mm:ss.xx] 歌詞文字
        /// 過濾空行和純英文括號行（背景人聲）
        /// </summary>
        private static List<(double TimeStamp, string Japanese)> ParseLrc(string lrc)
        {
            var result = new List<(double, string)>();
            var timestampRegex = new Regex(@"^\[(\d+):(\d+\.\d+)\](.*)$");

            foreach (var rawLine in lrc.Split('\n'))
            {
                var line = rawLine.Trim();
                var m = timestampRegex.Match(line);
                if (!m.Success) continue;

                var mins = double.Parse(m.Groups[1].Value);
                var secs = double.Parse(m.Groups[2].Value);
                var text = m.Groups[3].Value.Trim();

                // 跳過空行、背景聲與作詞/作曲/編曲等來源 metadata 行。
                if (LyricLineFilter.ShouldSkipSyncedLyricLine(text)) continue;

                result.Add((mins * 60 + secs, text));
            }

            return result;
        }
    }
}
