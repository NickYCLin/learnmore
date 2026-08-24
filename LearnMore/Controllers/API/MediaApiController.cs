using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using LearnMore.Controllers;
using LearnMore.Services;

namespace LearnMore.Controllers.API
{
    [ApiController]
    [Route("api/media")]
    public class MediaApiController : ControllerBase
    {
        #region 基本參數
        private readonly string _connectionString;
        private readonly ILogger<MediaApiController> _logger;

        public MediaApiController(IConfiguration configuration, ILogger<MediaApiController> logger)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _logger = logger;
        }
        #endregion

        #region 確認音樂是否已存在於資料庫中
        [HttpPost("CheckYouTubeLink")]
        public async Task<IActionResult> CheckYouTubeLink([FromBody] string youtubeUrl)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            string? requestedVideoId = YouTubeVideoIdExtractor.Extract(youtubeUrl);
            const string query = @"
SELECT SongUid, YouTubeVideoUrl
FROM Songs
WHERE YouTubeVideoUrl IS NOT NULL AND LTRIM(RTRIM(YouTubeVideoUrl)) <> ''";

            await using (SqlConnection conn = new SqlConnection(_connectionString))
            await using (SqlCommand cmd = new SqlCommand(query, conn))
            {
                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string songUid = reader.GetString(0);
                    string storedUrl = reader.GetString(1);
                    bool exists = !string.IsNullOrWhiteSpace(requestedVideoId)
                        ? string.Equals(YouTubeVideoIdExtractor.Extract(storedUrl), requestedVideoId, StringComparison.Ordinal)
                        : string.Equals(storedUrl.Trim(), youtubeUrl?.Trim(), StringComparison.OrdinalIgnoreCase);

                    if (exists)
                    {
                        return Ok(new { exists = true, songUid });
                    }
                }
            }

            return Ok(new { exists = false });
        }
        #endregion

        #region 刪除歌曲
        /// <summary>
        /// 刪除歌曲（需要登入）
        /// </summary>
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteSong([FromBody] DeleteSongRequest request)
        {
            // 驗證登入
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                _logger.LogWarning("DeleteSong: 未登入的請求");
                return Unauthorized(new { error = "請先登入" });
            }

            // 驗證參數
            if (string.IsNullOrWhiteSpace(request?.SongUid))
            {
                return BadRequest(new { error = "songUid 為必填" });
            }

            // 驗證 SongUid 格式（防止 SQL Injection）
            if (!Regex.IsMatch(request.SongUid, @"^[A-Za-z0-9_-]+$"))
            {
                return BadRequest(new { error = "songUid 格式無效" });
            }

            _logger.LogInformation("DeleteSong: 使用者 {User} 刪除歌曲 {SongUid}", userEmail, request.SongUid);

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                // 1. 檢查歌曲是否存在
                var checkQuery = "SELECT COUNT(1) FROM Songs WHERE SongUid = @SongUid";
                using (var checkCmd = new SqlCommand(checkQuery, conn))
                {
                    checkCmd.Parameters.AddWithValue("@SongUid", request.SongUid);
                    var count = (int)(await checkCmd.ExecuteScalarAsync() ?? 0);
                    if (count == 0)
                    {
                        return NotFound(new { error = $"歌曲 {request.SongUid} 不存在" });
                    }
                }

                if (!await CanDeleteSongAsync(conn, userEmail, request.SongUid))
                {
                    _logger.LogWarning("DeleteSong: 使用者 {User} 嘗試刪除無權限歌曲 {SongUid}", userEmail, request.SongUid);
                    return Forbid();
                }

                // 2. 刪除歌詞資料表（使用動態 SQL，但 SongUid 已驗證過格式）
                var tableName = $"Songs_{request.SongUid}";
                var dropTableQuery = $"IF OBJECT_ID('dbo.{tableName}', 'U') IS NOT NULL DROP TABLE dbo.[{tableName}]";
                using (var dropCmd = new SqlCommand(dropTableQuery, conn))
                {
                    await dropCmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("DeleteSong: 已刪除資料表 {Table}", tableName);
                }

                // 3. 刪除主表記錄
                var deleteSongQuery = "DELETE FROM Songs WHERE SongUid = @SongUid";
                using (var deleteCmd = new SqlCommand(deleteSongQuery, conn))
                {
                    deleteCmd.Parameters.AddWithValue("@SongUid", request.SongUid);
                    var rowsAffected = await deleteCmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("DeleteSong: 已刪除 Songs 記錄，影響 {Rows} 列", rowsAffected);
                }

                return Ok(new { success = true, deleted = request.SongUid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteSong: 刪除歌曲 {SongUid} 失敗", request.SongUid);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static async Task<bool> CanDeleteSongAsync(SqlConnection conn, string userEmail, string songUid)
        {
            const string query = @"
SELECT U.Id,
       ISNULL(U.Manager, 0) AS Manager,
       ISNULL(U.Producer, '') AS Producer,
       S.AddedByUserId
FROM Users U
LEFT JOIN Songs S ON S.SongUid = @SongUid
WHERE U.Email = @Email";

            await using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Email", userEmail);
            cmd.Parameters.AddWithValue("@SongUid", songUid);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return false;
            }

            int userId = Convert.ToInt32(reader["Id"]);
            bool isManager = Convert.ToBoolean(reader["Manager"]);
            if (isManager)
            {
                return true;
            }

            if (reader["AddedByUserId"] != DBNull.Value && Convert.ToInt32(reader["AddedByUserId"]) == userId)
            {
                return true;
            }

            string producerList = reader["Producer"].ToString() ?? string.Empty;
            return producerList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(songUid, StringComparer.Ordinal);
        }

        public class DeleteSongRequest
        {
            public string? SongUid { get; set; }
        }
        #endregion
    }
}
