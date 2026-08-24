using System.Text.RegularExpressions;
using System.Text.Json;

namespace LearnMore.Services
{
    /// <summary>
    /// 從網易雲音樂公開 API 搜尋並解析 LRC 時間戳歌詞。
    /// 不需要 API Key，User-Agent 帶 Mozilla/5.0 即可。
    /// </summary>
    public class NetEaseLrcService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NetEaseLrcService> _logger;

        private const string SearchUrl = "https://music.163.com/api/search/get";
        private const string LyricUrl = "https://music.163.com/api/song/lyric";

        public NetEaseLrcService(IHttpClientFactory httpClientFactory, ILogger<NetEaseLrcService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// 搜尋歌曲並回傳 LRC 時間戳歌詞行。
        /// 回傳 null 表示找不到或解析失敗。
        /// </summary>
        public async Task<List<(double TimeStamp, string Japanese)>?> FetchLyricsAsync(
            string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                return null;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.Timeout = TimeSpan.FromSeconds(10);

                // Step 1：搜尋取 songId。候選必須符合曲名/歌手，避免拿到同歌手其他歌。
                var songId = default(long?);
                foreach (var titleCandidate in SyncedLyricsMetadataMatcher.BuildTitleCandidates(title))
                {
                    var query = Uri.EscapeDataString($"{titleCandidate} {artist}".Trim());
                    var searchUri = $"{SearchUrl}?s={query}&type=1&limit=10";

                    var searchResp = await client.GetAsync(searchUri);
                    if (!searchResp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("NetEase search failed: HTTP {Code}", (int)searchResp.StatusCode);
                        continue;
                    }

                    var searchJson = await searchResp.Content.ReadAsStringAsync();
                    songId = ExtractBestSongId(searchJson, title, artist);
                    if (songId != null)
                    {
                        break;
                    }
                }

                if (songId == null)
                {
                    _logger.LogInformation("NetEase: 找不到歌曲 {Title} {Artist}", title, artist);
                    return null;
                }

                _logger.LogInformation("NetEase: 找到 songId={Id}，準備取 LRC", songId);

                // Step 2：取 LRC 歌詞
                var lyricUri = $"{LyricUrl}?id={songId}&lv=1";
                var lyricResp = await client.GetAsync(lyricUri);
                if (!lyricResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NetEase lyric fetch failed: HTTP {Code}", (int)lyricResp.StatusCode);
                    return null;
                }

                var lyricJson = await lyricResp.Content.ReadAsStringAsync();
                var lrcText = ExtractLrcText(lyricJson);
                if (string.IsNullOrWhiteSpace(lrcText))
                {
                    _logger.LogInformation("NetEase: songId={Id} 無 LRC 歌詞", songId);
                    return null;
                }

                var lines = ParseLrc(lrcText);
                if (lines.Count < 3)
                {
                    _logger.LogInformation("NetEase: LRC 行數太少（{Count}），跳過", lines.Count);
                    return null;
                }

                _logger.LogInformation("NetEase: 解析完成，共 {Count} 行", lines.Count);
                return lines;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NetEaseLrcService.FetchLyricsAsync 發生例外 {Title} {Artist}", title, artist);
                return null;
            }
        }

        public async Task<string?> ResolvePrimaryArtistAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.Timeout = TimeSpan.FromSeconds(10);

                var query = Uri.EscapeDataString(title.Trim());
                var searchUri = $"{SearchUrl}?s={query}&type=1&limit=3";
                var searchResp = await client.GetAsync(searchUri);
                if (!searchResp.IsSuccessStatusCode)
                    return null;

                var searchJson = await searchResp.Content.ReadAsStringAsync();
                return ExtractPrimaryArtistName(searchJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NetEaseLrcService.ResolvePrimaryArtistAsync 失敗 {Title}", title);
                return null;
            }
        }

        public static string? ExtractPrimaryArtistName(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("result", out var result)) return null;
                if (!result.TryGetProperty("songs", out var songs)) return null;
                if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0) return null;

                var first = songs[0];
                if (!first.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() == 0)
                    return null;

                var artist = artists[0];
                if (artist.TryGetProperty("name", out var nameProp))
                    return nameProp.GetString();
            }
            catch { }
            return null;
        }

        public static long? ExtractBestSongId(string json, string title, string artist)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 結構：{ "result": { "songs": [ { "id": 12345, ... }, ... ] } }
                if (!root.TryGetProperty("result", out var result)) return null;
                if (!result.TryGetProperty("songs", out var songs)) return null;
                if (songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0) return null;

                foreach (var song in songs.EnumerateArray())
                {
                    var songName = song.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (!SyncedLyricsMetadataMatcher.IsLikelyTrackMatch(songName, title))
                    {
                        continue;
                    }

                    var artistNames = new List<string>();
                    if (song.TryGetProperty("artists", out var artists)
                        && artists.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var artistItem in artists.EnumerateArray())
                        {
                            if (artistItem.TryGetProperty("name", out var artistNameProp))
                            {
                                var artistName = artistNameProp.GetString();
                                if (!string.IsNullOrWhiteSpace(artistName))
                                {
                                    artistNames.Add(artistName);
                                }
                            }
                        }
                    }

                    var combinedArtists = string.Join(" ", artistNames);
                    if (!SyncedLyricsMetadataMatcher.IsLikelyArtistMatch(combinedArtists, artist))
                    {
                        continue;
                    }

                    if (song.TryGetProperty("id", out var idProp))
                        return idProp.GetInt64();
                }
            }
            catch { /* 解析失敗靜默忽略 */ }
            return null;
        }

        private static string? ExtractLrcText(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 結構：{ "lrc": { "lyric": "[mm:ss.xx]..." } }
                if (!root.TryGetProperty("lrc", out var lrc)) return null;
                if (!lrc.TryGetProperty("lyric", out var lyric)) return null;
                return lyric.GetString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 解析 LRC 格式字串，跳過 meta 標籤與空行。
        /// 支援 [mm:ss.xx]、[mm:ss.xxx]、[mm:ss] 格式。
        /// </summary>
        private static List<(double TimeStamp, string Japanese)> ParseLrc(string lrc)
        {
            var result = new List<(double, string)>();

            // 匹配時間戳：[分:秒(.毫秒)] 歌詞
            var timestampRegex = new Regex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");

            // meta 標籤（[ar:]、[ti:]、[al:]、[by:]、[offset:] 等），直接跳過
            var metaRegex = new Regex(@"^\[[a-zA-Z]+:");

            foreach (var rawLine in lrc.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (metaRegex.IsMatch(line)) continue;

                var m = timestampRegex.Match(line);
                if (!m.Success) continue;

                var mins = double.Parse(m.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                var secs = double.Parse(m.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                var text = m.Groups[3].Value.Trim();

                if (LyricLineFilter.ShouldSkipSyncedLyricLine(text)) continue;

                result.Add((mins * 60 + secs, text));
            }

            return result;
        }
    }
}
