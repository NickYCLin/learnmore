using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;

namespace LearnMore.Controllers.API
{
    [ApiController]
    [Route("api/[controller]")]
    public class YoutubeController : ControllerBase
    {
        #region 基本參數
        private readonly string _connectionString;
        // 修改說明：移除 hardcoded API Key，改為透過 IConfiguration 讀取
        private readonly string _apiKey;
        private readonly IConfiguration _configuration;

        public YoutubeController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            // 修改說明：從 appsettings.json 的 YouTube:ApiKey 讀取，避免 API Key 寫死在程式碼中
            _apiKey = configuration["YouTube:ApiKey"] ?? string.Empty;
        }
        #endregion

        #region 取得影片資訊
        [HttpGet("video-info")]
        public async Task<IActionResult> GetVideoInfo([FromQuery] string videoId)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (string.IsNullOrWhiteSpace(videoId))
                return BadRequest("請提供 videoId");

            var youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _apiKey,
                ApplicationName = "LearnMore-YT-Viewer"
            });

            var request = youtubeService.Videos.List("snippet,statistics");
            request.Id = videoId;

            var response = await request.ExecuteAsync();

            if (response.Items.Count == 0)
                return NotFound("找不到影片");

            var video = response.Items[0];
            var channelId = video.Snippet.ChannelId;

            // 查詢頻道資訊以取得頭像
            var channelRequest = youtubeService.Channels.List("snippet");
            channelRequest.Id = channelId;
            var channelResponse = await channelRequest.ExecuteAsync();

            string? channelThumbnailUrl = null;
            if (channelResponse.Items.Count > 0)
            {
                channelThumbnailUrl = channelResponse.Items[0].Snippet.Thumbnails.Default__.Url;
            }

            var result = new
            {
                Title = video.Snippet.Title,
                Channel = video.Snippet.ChannelTitle,
                ChannelThumbnail = channelThumbnailUrl,
                ViewCount = video.Statistics.ViewCount,
                LikeCount = video.Statistics.LikeCount,
                PublishedAt = video.Snippet.PublishedAtDateTimeOffset
            };

            return Ok(result);
        }
        #endregion

        #region 執行資料庫內全部的歌曲，跑Youtube Api
        [HttpPost("update-all-songs-data")]
        public async Task<IActionResult> UpdateAllSongsData()
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            var youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _apiKey,
                ApplicationName = "LearnMore-YT-Viewer"
            });

            var songs = new List<(string SongUid, string VideoUrl)>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var cmd = new SqlCommand("SELECT SongUid, YouTubeVideoUrl FROM Songs WHERE YouTubeVideoUrl IS NOT NULL", connection);
                using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    songs.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            int success = 0, failed = 0;

            foreach (var song in songs)
            {
                var videoId = ExtractVideoId(song.VideoUrl);
                if (string.IsNullOrEmpty(videoId))
                {
                    failed++;
                    continue;
                }

                try
                {
                    var request = youtubeService.Videos.List("snippet,statistics");
                    request.Id = videoId;

                    var response = await request.ExecuteAsync();
                    var video = response.Items.FirstOrDefault();
                    if (video == null)
                    {
                        failed++;
                        continue;
                    }

                    // ✅ 安全轉為 int（上限保護）
                    var viewRaw = video.Statistics.ViewCount ?? 0;
                    var likeRaw = video.Statistics.LikeCount ?? 0;
                    int viewCount = (viewRaw > int.MaxValue) ? int.MaxValue : Convert.ToInt32(viewRaw);
                    int likeCount = (likeRaw > int.MaxValue) ? int.MaxValue : Convert.ToInt32(likeRaw);

                    var channelId = video.Snippet.ChannelId;

                    // 查詢頻道資訊以取得頭像
                    var channelRequest = youtubeService.Channels.List("snippet");
                    channelRequest.Id = channelId;
                    var channelResponse = await channelRequest.ExecuteAsync();

                    string? channelThumbnailUrl = null;
                    if (channelResponse.Items.Count > 0)
                    {
                        channelThumbnailUrl = channelResponse.Items[0].Snippet.Thumbnails.Default__.Url;
                    }

                    var now = DateTime.Now;

                    using var conn = new SqlConnection(_connectionString);
                    await conn.OpenAsync();

                    // ✅ Step 1: 插入歷史資料 SongsDataHistory
                    var insertHistory = new SqlCommand(@"
                INSERT INTO SongsDataHistory (SongUid, ViewCount, LikeCount, UpdatedAt)
                VALUES (@SongUid, @ViewCount, @LikeCount, @UpdatedAt)", conn);
                    insertHistory.Parameters.AddWithValue("@SongUid", song.SongUid);
                    insertHistory.Parameters.AddWithValue("@ViewCount", viewCount);
                    insertHistory.Parameters.AddWithValue("@LikeCount", likeCount);
                    insertHistory.Parameters.AddWithValue("@UpdatedAt", now);
                    await insertHistory.ExecuteNonQueryAsync();

                    // ✅ Step 2: 查找一週 & 一個月前的資料
                    int viewWeek = await GetNearestViewCount(conn, song.SongUid, now.AddDays(-7), now);
                    int viewMonth = await GetNearestViewCount(conn, song.SongUid, now.AddMonths(-1), now);
                    int likeWeek = await GetNearestLikeCount(conn, song.SongUid, now.AddDays(-7), now);
                    int likeMonth = await GetNearestLikeCount(conn, song.SongUid, now.AddMonths(-1), now);

                    int viewWeeklyGrowth = (viewCount - viewWeek == viewCount) ? 0 : viewCount - viewWeek;
                    int viewMonthlyGrowth = (viewCount - viewMonth == viewCount) ? 0 : viewCount - viewMonth;
                    int likeWeeklyGrowth = (likeCount - likeWeek == likeCount) ? 0 : likeCount - likeWeek;
                    int likeMonthlyGrowth = (likeCount - likeMonth == likeCount) ? 0 : likeCount - likeMonth;

                    // 2.5 將創作者頭像更新到Songs資料表
                    var updateSongCmd = new SqlCommand(@"UPDATE Songs SET ChannelThumbnailUrl=@channelThumbnailUrl WHERE SongUid = @SongUid", conn);
                    updateSongCmd.Parameters.AddWithValue("@SongUid", song.SongUid);
                    updateSongCmd.Parameters.AddWithValue("@channelThumbnailUrl", (object?)channelThumbnailUrl ?? DBNull.Value);
                    await updateSongCmd.ExecuteNonQueryAsync();

                    // ✅ Step 3: 更新或插入 SongsData（最新）
                    var checkCmd = new SqlCommand("SELECT COUNT(*) FROM SongsData WHERE SongUid = @SongUid", conn);
                    checkCmd.Parameters.AddWithValue("@SongUid", song.SongUid);
                    int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync() ?? 0);

                    if (exists > 0)
                    {
                        var updateCmd = new SqlCommand(@"
                    UPDATE SongsData SET
                        ViewCount = @ViewCount,
                        LikeCount = @LikeCount,
                        ViewCount_WeeklyGrowth = @ViewCountWeekly,
                        ViewCount_MonthlyGrowth = @ViewCountMonthly,
                        LikeCount_WeeklyGrowth = @LikeCountWeekly,
                        LikeCount_MonthlyGrowth = @LikeCountMonthly,
                        UpdatedAt = @UpdatedAt
                    WHERE SongUid = @SongUid", conn);
                        updateCmd.Parameters.AddWithValue("@ViewCount", viewCount);
                        updateCmd.Parameters.AddWithValue("@LikeCount", likeCount);
                        updateCmd.Parameters.AddWithValue("@ViewCountWeekly", viewWeeklyGrowth);
                        updateCmd.Parameters.AddWithValue("@ViewCountMonthly", viewMonthlyGrowth);
                        updateCmd.Parameters.AddWithValue("@LikeCountWeekly", likeWeeklyGrowth);
                        updateCmd.Parameters.AddWithValue("@LikeCountMonthly", likeMonthlyGrowth);
                        updateCmd.Parameters.AddWithValue("@UpdatedAt", now);
                        updateCmd.Parameters.AddWithValue("@SongUid", song.SongUid);
                        await updateCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        var insertCmd = new SqlCommand(@"
                    INSERT INTO SongsData (
                        SongUid, ViewCount, LikeCount,
                        ViewCount_WeeklyGrowth, ViewCount_MonthlyGrowth,
                        LikeCount_WeeklyGrowth, LikeCount_MonthlyGrowth,
                        UpdatedAt
                    ) VALUES (
                        @SongUid, @ViewCount, @LikeCount,
                        @ViewCountWeekly, @ViewCountMonthly,
                        @LikeCountWeekly, @LikeCountMonthly,
                        @UpdatedAt
                    )", conn);
                        insertCmd.Parameters.AddWithValue("@SongUid", song.SongUid);
                        insertCmd.Parameters.AddWithValue("@ViewCount", viewCount);
                        insertCmd.Parameters.AddWithValue("@LikeCount", likeCount);
                        insertCmd.Parameters.AddWithValue("@ViewCountWeekly", viewWeeklyGrowth);
                        insertCmd.Parameters.AddWithValue("@ViewCountMonthly", viewMonthlyGrowth);
                        insertCmd.Parameters.AddWithValue("@LikeCountWeekly", likeWeeklyGrowth);
                        insertCmd.Parameters.AddWithValue("@LikeCountMonthly", likeMonthlyGrowth);
                        insertCmd.Parameters.AddWithValue("@UpdatedAt", now);
                        await insertCmd.ExecuteNonQueryAsync();
                    }

                    success++;
                }
                catch
                {
                    failed++;
                }
            }

            return Ok(new { Updated = success, Failed = failed, Total = songs.Count });
        }
        private string? ExtractVideoId(string url) => YouTubeVideoIdExtractor.Extract(url);
        private async Task<int> GetNearestViewCount(SqlConnection conn, string songUid, DateTime targetDate, DateTime now)
        {
            var cmd = new SqlCommand(@"
        SELECT TOP 1 ViewCount FROM SongsDataHistory
        WHERE SongUid = @SongUid AND UpdatedAt <= @Now
        ORDER BY ABS(DATEDIFF(SECOND, @TargetDate, UpdatedAt))", conn);
            cmd.Parameters.AddWithValue("@SongUid", songUid);
            cmd.Parameters.AddWithValue("@TargetDate", targetDate);
            cmd.Parameters.AddWithValue("@Now", now);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }

        private async Task<int> GetNearestLikeCount(SqlConnection conn, string songUid, DateTime targetDate, DateTime now)
        {
            var cmd = new SqlCommand(@"
        SELECT TOP 1 LikeCount FROM SongsDataHistory
        WHERE SongUid = @SongUid AND UpdatedAt <= @Now
        ORDER BY ABS(DATEDIFF(SECOND, @TargetDate, UpdatedAt))", conn);
            cmd.Parameters.AddWithValue("@SongUid", songUid);
            cmd.Parameters.AddWithValue("@TargetDate", targetDate);
            cmd.Parameters.AddWithValue("@Now", now);

            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
        }
        #endregion
    }
}
