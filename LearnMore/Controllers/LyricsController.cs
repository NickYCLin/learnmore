using LearnMore.Models;
using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using YoutubeExplode.Channels;

namespace LearnMore.Controllers
{
    [Route("Lyrics")]
    public class LyricsController : Controller
    {
        #region 基本參數
        private static readonly Regex SafeSongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        private readonly string _connectionString;
        private readonly IWebHostEnvironment _environment;
        private static readonly string[] SupportedAudioStemExtensions = new[] { ".mp3", ".m4a", ".wav", ".flac", ".ogg" };

        public LyricsController(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _environment = environment;
        }
        #endregion

        #region 歌曲播放頁面
        [HttpGet("{songUid}")]
        public IActionResult Index(string songUid)
        {
            var email = HttpContext.Session.GetString("Email"); // 取得登入使用者的 Email

            bool isManage = false; // 是否為管理員
            bool isEnableRoman = true; // 預設啟用羅馬字
            bool isEnableAuto = true; // 是否啟用自動翻譯
            int? currentUserId = null; // 當前使用者 ID
            if (!string.IsNullOrWhiteSpace(email))
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    string query = "SELECT Id, Manager, EnableRoman, EnableAuto FROM Users WHERE Email = @UserEmail";

                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.Add("@UserEmail", SqlDbType.NVarChar, 500).Value = email;

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read()) // 如果有找到對應的 Email
                            {
                                currentUserId = reader.GetInt32(0); // Id 欄位
                                isManage = !reader.IsDBNull(1) && reader.GetBoolean(1); // Manager 欄位
                                isEnableRoman = !reader.IsDBNull(2) && reader.GetBoolean(2); // EnableRoman 欄位
                                isEnableAuto = !reader.IsDBNull(3) && reader.GetBoolean(3); // EnableAuto 欄位
                            }
                        }
                    }
                }
            }

            var song = GetSongByUid(songUid);
            if (song == null)
            {
                return RedirectToAction("Index", "Home");
            }

            var lyrics = GetLyricsBySongUid(songUid, true);
            var comments = GetCommentsBySongUid(songUid); // **載入留言**

            // 判斷是否可以編輯時間戳：管理員、納西妲 (Id=6)、或上傳者
            bool canEditTimestamp = isManage ||
                                    currentUserId == 6 || // 納西妲帳號
                                    (currentUserId.HasValue && song.AddedByUserId == currentUserId);

            var model = new SongViewModel
            {
                LyricID = song.LyricID,
                Title = song.Title,
                Artist = song.Artist,
                Performer = song.Performer ?? string.Empty,
                Translator = song.Translator ?? string.Empty,
                TranslationSource = song.TranslationSource ?? string.Empty,
                VideoPath = song.YouTubeVideoUrl ?? string.Empty,
                InstrumentalAudioUrl = song.InstrumentalAudioUrl ?? string.Empty,
                VocalsAudioUrl = song.VocalsAudioUrl ?? string.Empty,
                SongUid = song.SongUid ?? string.Empty,
                Lyrics = lyrics,
                Comments = comments,
                UserEmail = email ?? string.Empty,
                IsManage = isManage, // **是否為管理員**
                IsEnableRoman = isEnableRoman, // **是否啟用羅馬字**
                IsEnableAuto = isEnableAuto, // **是否啟用自動翻譯**
                CanEditTimestamp = canEditTimestamp, // **是否可編輯時間戳**
                AddedByUserId = song.AddedByUserId, // **上傳者 ID**
                HighAccuracyStatus = song.HighAccuracyStatus,
                HighAccuracyStatusReason = song.HighAccuracyStatusReason
            };

            bool hasPlaceholderChinese = lyrics.Any(line => string.Equals(line.Chinese, "翻譯中...", StringComparison.Ordinal));
            bool isHighAccuracyRunning = string.Equals(song.HighAccuracyStatus, "high_accuracy_pending", StringComparison.OrdinalIgnoreCase)
                || string.Equals(song.HighAccuracyStatus, "high_accuracy_processing", StringComparison.OrdinalIgnoreCase);
            bool needsHighAccuracyReview = string.Equals(song.HighAccuracyStatus, "high_accuracy_needs_review", StringComparison.OrdinalIgnoreCase)
                || string.Equals(song.HighAccuracyStatus, "high_accuracy_failed", StringComparison.OrdinalIgnoreCase);
            bool hasNoLyricsYet = lyrics.Count == 0;

            model.HasPendingLyricsProcessing = hasNoLyricsYet || needsHighAccuracyReview || isHighAccuracyRunning || hasPlaceholderChinese;
            model.LyricsProcessingTitle = needsHighAccuracyReview
                ? "這首歌需要檢查"
                : "這首歌還在整理中";
            model.LyricsProcessingMessage = needsHighAccuracyReview
                ? $"這首歌的高精度校正未通過驗證，歌詞或秒數可能不準。{song.HighAccuracyStatusReason}"
                : hasNoLyricsYet
                    ? "這首歌剛上架，歌詞還在建立中，請稍後再重新整理哦。"
                    : isHighAccuracyRunning
                        ? "這首歌的高精度校正還在背景處理中，歌詞可能還會再微調哦。"
                        : hasPlaceholderChinese
                            ? "這首歌剛上架，翻譯與注音還在背景整理中；現在先顯示已產生的歌詞內容呀。"
                            : string.Empty;

            return View(model);
        }

        private List<CommentModel> GetCommentsBySongUid(string songUid)
        {
            string appBasePath = HttpContext.Request.PathBase;
            List<CommentModel> comments = new List<CommentModel>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                string query = @"
            SELECT [CommentId]
                  ,[SongUid]
                  ,[UserEmail]
                  ,[UserName]
                  ,[Message]
                  ,[TimeStamp]
                  ,[IsPrivate]
                  ,[Avatar]
              FROM [V_Comments]
              WHERE [SongUid] = @SongUid
              ORDER BY [TimeStamp] DESC";  // 最新留言排在最上方

                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@SongUid", songUid);

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string? avatar = reader["Avatar"] != DBNull.Value ? reader["Avatar"].ToString() : null;
                            string userName = reader.IsDBNull(3) ? "訪客" : reader.GetString(3);

                            comments.Add(new CommentModel
                            {
                                CommentId = reader.GetGuid(0),
                                SongUid = reader.GetString(1),
                                UserEmail = reader.IsDBNull(2) ? "--" : reader.GetString(2),
                                UserName = reader.IsDBNull(3) ? "訪客" : reader.GetString(3),
                                Message = reader.GetString(4),
                                TimeStamp = reader.GetDateTime(5),
                                IsPrivate = !reader.IsDBNull(6) && reader.GetBoolean(6),
                                Avatar = !string.IsNullOrEmpty(avatar)
                                ? $"{appBasePath}{avatar}"
                            : (string.IsNullOrEmpty(userName) || userName == "訪客")
                                ? $"{appBasePath}/images/visitor.png"
                                : $"{appBasePath}/images/default-avatar.png"
                            });
                        }
                    }
                }
            }

            return comments;
        }

        private Songs? GetSongByUid(string songUid)
        {
            Songs? song = null;

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string query = @"SELECT Title, Artist,
       CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN [Performer] ELSE NULL END AS [Performer],
       Translator, TranslationSource, YouTubeVideoUrl, AddedByUserId,
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NOT NULL THEN [HighAccuracyStatus] ELSE NULL END AS [HighAccuracyStatus],
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NOT NULL THEN [HighAccuracyStatusReason] ELSE NULL END AS [HighAccuracyStatusReason]
FROM Songs WHERE SongUid = @SongUid";
                SqlCommand command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@SongUid", songUid);

                connection.Open();
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        song = new Songs
                        {
                            Title = reader["Title"].ToString() ?? string.Empty,
                            Artist = reader["Artist"].ToString() ?? string.Empty,
                            Performer = reader["Performer"].ToString() ?? string.Empty,
                            Translator = reader["Translator"].ToString() ?? string.Empty,
                            TranslationSource = reader["TranslationSource"].ToString() ?? string.Empty,
                            YouTubeVideoUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
                            InstrumentalAudioUrl = GetInstrumentalAudioUrl(songUid),
                            VocalsAudioUrl = GetVocalsAudioUrl(songUid),
                            SongUid = songUid,
                            AddedByUserId = reader["AddedByUserId"] != DBNull.Value ? Convert.ToInt32(reader["AddedByUserId"]) : (int?)null,
                            HighAccuracyStatus = reader["HighAccuracyStatus"].ToString(),
                            HighAccuracyStatusReason = reader["HighAccuracyStatusReason"].ToString()
                        };
                    }
                }
            }

            return song;
        }

        private string? GetInstrumentalAudioUrl(string songUid)
        {
            return GetAudioStemUrl(songUid, "instrumental");
        }

        private string? GetVocalsAudioUrl(string songUid)
        {
            return GetAudioStemUrl(songUid, "vocals");
        }

        private string? GetAudioStemUrl(string songUid, string stemKind)
        {
            var databaseUrl = GetAudioStemPublicUrl(songUid, stemKind);
            if (!string.IsNullOrWhiteSpace(databaseUrl))
            {
                return NormalizePublicAudioUrl(databaseUrl);
            }

            return GetStaticAudioStemUrl(songUid, stemKind);
        }

        private string? GetAudioStemPublicUrl(string songUid, string stemKind)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
IF OBJECT_ID('dbo.SongAudioStems', 'U') IS NOT NULL
BEGIN
    SELECT TOP (1) PublicUrl, UpdatedAt
    FROM dbo.SongAudioStems
    WHERE SongUid = @SongUid
      AND StemKind = @StemKind
      AND NULLIF(LTRIM(RTRIM(PublicUrl)), '') IS NOT NULL
    ORDER BY CreatedAt DESC
END", connection))
            {
                command.Parameters.Add("@SongUid", SqlDbType.NVarChar, 500).Value = songUid;
                command.Parameters.Add("@StemKind", SqlDbType.NVarChar, 50).Value = stemKind;

                connection.Open();
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                var publicUrl = reader["PublicUrl"]?.ToString();
                if (string.IsNullOrWhiteSpace(publicUrl))
                {
                    return null;
                }

                var updatedAt = reader["UpdatedAt"] != DBNull.Value
                    ? Convert.ToDateTime(reader["UpdatedAt"], CultureInfo.InvariantCulture)
                    : DateTime.UtcNow;
                return AppendAudioStemVersion(publicUrl, updatedAt);
            }
        }

        private string? GetStaticAudioStemUrl(string songUid, string stemKind)
        {
            if (!Regex.IsMatch(songUid, "^[0-9A-Za-z_-]+$"))
            {
                return null;
            }

            var stemDirectory = Path.Combine(_environment.WebRootPath, "audio-stems", songUid);
            if (!Directory.Exists(stemDirectory))
            {
                return null;
            }

            var stemFile = Directory.EnumerateFiles(stemDirectory)
                .Where(path => SupportedAudioStemExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path =>
                {
                    var fileStem = Path.GetFileNameWithoutExtension(path);
                    return fileStem.Equals(stemKind, StringComparison.OrdinalIgnoreCase)
                        || fileStem.Contains(stemKind, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Equals(stemKind, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => Path.GetFileNameWithoutExtension(path).Contains(stemKind, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => Path.GetFileName(path))
                .FirstOrDefault();

            if (stemFile is null)
            {
                return null;
            }

            var publicUrl = $"~/audio-stems/{Uri.EscapeDataString(songUid)}/{Uri.EscapeDataString(Path.GetFileName(stemFile))}";
            return NormalizePublicAudioUrl(AppendAudioStemVersion(publicUrl, System.IO.File.GetLastWriteTimeUtc(stemFile)));
        }

        private static string AppendAudioStemVersion(string publicUrl, DateTime updatedAt)
        {
            var separator = publicUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return publicUrl + separator + "v=" + updatedAt.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        private string NormalizePublicAudioUrl(string publicUrl)
        {
            if (Uri.TryCreate(publicUrl, UriKind.Absolute, out _))
            {
                return publicUrl;
            }

            if (publicUrl.StartsWith("~/", StringComparison.Ordinal))
            {
                return Url.Content(publicUrl);
            }

            return Url.Content("~/" + publicUrl.TrimStart('/'));
        }

        private List<Lyrics> GetLyricsBySongUid(string songUid, bool enableRoman)
        {
            List<Lyrics> lyricsList = new List<Lyrics>();
            if (string.IsNullOrWhiteSpace(songUid) || !SafeSongUidPattern.IsMatch(songUid))
            {
                return lyricsList;
            }

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                // 動態查詢語句，根據是否需要 Roman 字段來決定 SQL
                string dynamicTableName = $"[Songs_{songUid}]";
                string columns = enableRoman
                    ? "LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman"
                    : "LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby";
                string getLyricsQuery = $"SELECT {columns} FROM {dynamicTableName} ORDER BY TimeStamp";

                using (SqlCommand getLyricsCommand = new SqlCommand(getLyricsQuery, connection))
                {
                    using (SqlDataReader reader = getLyricsCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var timeStampText = reader["TimeStamp"].ToString();
                            var japaneseRuby = reader["JapaneseRuby"].ToString() ?? string.Empty;
                            var japanese = reader["Japanese"].ToString() ?? string.Empty;
                            var lyrics = new Lyrics
                            {
                                LyricID = Convert.ToInt32(reader["LyricID"]),
                                TimeStamp = float.TryParse(timeStampText, out var parsedTimeStamp) ? parsedTimeStamp : 0f,
                                Japanese = !string.IsNullOrWhiteSpace(japaneseRuby) ? japaneseRuby : japanese,
                                Chinese = reader["Chinese"].ToString() ?? string.Empty
                            };

                            if (enableRoman)
                            {
                                lyrics.Roman = reader["Roman"].ToString() ?? string.Empty;
                            }

                            lyricsList.Add(lyrics);
                        }
                    }
                }
            }

            return lyricsList;
        }

        [HttpPost]
        [Route("UpdateUserSettings")]
        public IActionResult UpdateUserSettings([FromBody] UserSettingsUpdateModel model)
        {
            var email = HttpContext.Session.GetString("Email"); // 確保使用者已登入
            if (string.IsNullOrWhiteSpace(email))
            {
                return Json(new { success = false, message = "未登入" });
            }

            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    string query = "UPDATE Users SET EnableAuto = @EnableAuto, EnableRoman = @EnableRoman WHERE Email = @UserEmail";

                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.Add("@EnableAuto", SqlDbType.Bit).Value = model.IsEnableAuto;
                        cmd.Parameters.Add("@EnableRoman", SqlDbType.Bit).Value = model.IsEnableRoman;
                        cmd.Parameters.Add("@UserEmail", SqlDbType.NVarChar, 500).Value = email;

                        int rowsAffected = cmd.ExecuteNonQuery();
                        if (rowsAffected > 0)
                        {
                            return Json(new { success = true, message = "設定已更新" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "資料庫錯誤: " + ex.Message });
            }

            return Json(new { success = false, message = "更新失敗" });
        }

        public class UserSettingsUpdateModel
        {
            public bool IsEnableAuto { get; set; }
            public bool IsEnableRoman { get; set; }
        }
        #endregion

        #region 留言板
        [HttpPost]
        [Route("AddComment")]
        public async Task<IActionResult> AddComment()
        {
            string appBasePath = HttpContext.Request.PathBase;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                string requestBody = await reader.ReadToEndAsync();

                try
                {
                    CommentModel? model = JsonSerializer.Deserialize<CommentModel>(requestBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (model == null || string.IsNullOrWhiteSpace(model.Message))
                    {
                        return Json(new { success = false, message = "請確認留言內容與歌曲 ID 皆不為空" });
                    }

                    string? UserEmail = HttpContext.Session.GetString("Email"); // 取得登入使用者的 Email
                    if (model.IsPrivate && string.IsNullOrWhiteSpace(UserEmail))
                    {
                        return Json(new { success = false, message = "登入後才能使用私密留言" });
                    }

                    Guid newId;

                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        string sql = @"
                DECLARE @NewCommentId uniqueidentifier = NEWID();
                INSERT INTO Comments (CommentId, SongUid, UserEmail, Message, TimeStamp, IsPrivate) 
                OUTPUT INSERTED.CommentId
                VALUES (@NewCommentId, @SongUid, @UserEmail, @Message, @TimeStamp, @IsPrivate);";

                        using (SqlCommand cmd = new SqlCommand(sql, conn))
                        {
                            cmd.Parameters.Add("@SongUid", SqlDbType.NVarChar, 500).Value = model.SongUid ?? string.Empty;
                            object userEmailValue = UserEmail is null ? DBNull.Value : UserEmail;
                            cmd.Parameters.Add("@UserEmail", SqlDbType.NVarChar, 500).Value = userEmailValue;
                            cmd.Parameters.Add("@Message", SqlDbType.NVarChar, 500).Value = model.Message ?? string.Empty;
                            cmd.Parameters.Add("@TimeStamp", SqlDbType.DateTime).Value = DateTime.Now;
                            cmd.Parameters.Add("@IsPrivate", SqlDbType.Bit).Value = model.IsPrivate;

                            newId = Guid.TryParse(cmd.ExecuteScalar()?.ToString(), out var parsedCommentId) ? parsedCommentId : Guid.Empty; // 獲取新插入的 CommentId
                        }
                    }

                    // 從 V_Comments 取得 UserName
                    string userName = "訪客"; // 預設為訪客
                    string avatar = ""; // 預設為空
                    using (SqlConnection conn = new SqlConnection(_connectionString))
                    {
                        conn.Open();
                        string query = "SELECT UserName, Avatar FROM V_Comments WHERE CommentId = @CommentId";

                        using (SqlCommand cmd = new SqlCommand(query, conn))
                        {
                            cmd.Parameters.Add("@CommentId", SqlDbType.UniqueIdentifier).Value = newId;
                            using (SqlDataReader readerC = cmd.ExecuteReader())
                            {
                                if (readerC.Read())
                                {
                                    userName = readerC["UserName"] != DBNull.Value ? (readerC["UserName"].ToString() ?? "訪客") : "訪客";
                                    avatar = readerC["Avatar"] != DBNull.Value ? (readerC["Avatar"].ToString() ?? string.Empty) : string.Empty;
                                }
                            }
                        }
                    }

                    return Json(new
                    {
                        success = true,
                        commentId = newId,
                        userName = userName,
                        userEmail = UserEmail,
                        message = model.Message,
                        timeStamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm"),
                        isPrivate = model.IsPrivate,
                        avatar = !string.IsNullOrEmpty(avatar)
                                ? $"{appBasePath}{avatar}"
                            : (string.IsNullOrEmpty(userName) || userName == "訪客")
                                ? $"{appBasePath}/images/visitor.png"
                                : $"{appBasePath}/images/default-avatar.png"
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("JSON 解析錯誤：" + ex.Message);
                    return Json(new { success = false, message = "JSON 解析失敗" });
                }
            }
        }
        #endregion

        #region 更新歌詞時間戳
        [HttpPost]
        [Route("UpdateTimestamp")]
        public IActionResult UpdateTimestamp([FromBody] UpdateTimestampModel model)
        {
            var email = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(email))
            {
                return Json(new { success = false, message = "請先登入" });
            }

            if (model == null || string.IsNullOrWhiteSpace(model.SongUid) || !SafeSongUidPattern.IsMatch(model.SongUid))
            {
                return Json(new { success = false, message = "Invalid songUid" });
            }

            // 檢查權限
            int? currentUserId = null;
            bool isManage = false;
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                string query = "SELECT Id, Manager FROM Users WHERE Email = @Email";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            currentUserId = reader.GetInt32(0);
                            isManage = !reader.IsDBNull(1) && reader.GetBoolean(1);
                        }
                    }
                }

                // 取得歌曲上傳者
                int? addedByUserId = null;
                string getSongQuery = "SELECT AddedByUserId FROM Songs WHERE SongUid = @SongUid";
                using (SqlCommand cmd = new SqlCommand(getSongQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@SongUid", model.SongUid);
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        addedByUserId = Convert.ToInt32(result);
                    }
                }

                // 權限檢查：管理員、納西妲 (Id=6)、或上傳者
                bool canEdit = isManage || currentUserId == 6 || (currentUserId.HasValue && addedByUserId == currentUserId);
                if (!canEdit)
                {
                    return Json(new { success = false, message = "權限不足" });
                }

                // 更新時間戳
                try
                {
                    string updateQuery = $"UPDATE [Songs_{model.SongUid}] SET TimeStamp = @NewTimestamp WHERE LyricID = @LyricID";
                    using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("@NewTimestamp", model.NewTimestamp);
                        cmd.Parameters.AddWithValue("@LyricID", model.LyricID);
                        int rows = cmd.ExecuteNonQuery();

                        if (rows > 0)
                        {
                            return Json(new { success = true, message = "時間戳已更新" });
                        }
                        else
                        {
                            return Json(new { success = false, message = "找不到該歌詞" });
                        }
                    }
                }
                catch (Exception ex)
                {
                    return Json(new { success = false, message = "更新失敗: " + ex.Message });
                }
            }
        }

        public class UpdateTimestampModel
        {
            public string SongUid { get; set; } = string.Empty;
            public int LyricID { get; set; }
            public double NewTimestamp { get; set; }
        }
        #endregion

        #region 回覆留言
        [HttpPost]
        [Route("ReplyComment")]
        public IActionResult ReplyComment([FromBody] CommentReplyModel reply)
        {
            var adminEmail = HttpContext.Session.GetString("Email"); // 取得管理員 Email
            if (string.IsNullOrEmpty(adminEmail))
            {
                return Json(new { success = false, message = "請先登入" });
            }

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                // 檢查是否為管理員
                string checkAdminQuery = "SELECT Manager FROM Users WHERE Email = @AdminEmail";
                using (SqlCommand checkCmd = new SqlCommand(checkAdminQuery, conn))
                {
                    checkCmd.Parameters.Add("@AdminEmail", SqlDbType.NVarChar, 500).Value = adminEmail;
                    object result = checkCmd.ExecuteScalar();
                    if (result == null || Convert.ToBoolean(result) == false)
                    {
                        return Json(new { success = false, message = "權限不足" });
                    }
                }

                // 儲存回覆
                string query = @"
            INSERT INTO CommentReplies (ReplyId, CommentId, AdminEmail, ReplyMessage, ReplyTime)
            VALUES (@ReplyId, @CommentId, @AdminEmail, @ReplyMessage, @ReplyTime);
        ";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.Add("@ReplyId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
                    cmd.Parameters.Add("@CommentId", SqlDbType.UniqueIdentifier).Value = reply.CommentId;
                    cmd.Parameters.Add("@AdminEmail", SqlDbType.NVarChar, 500).Value = adminEmail;
                    cmd.Parameters.Add("@ReplyMessage", SqlDbType.NVarChar, 2000).Value = reply.ReplyMessage;
                    cmd.Parameters.Add("@ReplyTime", SqlDbType.DateTime).Value = DateTime.Now;

                    cmd.ExecuteNonQuery();
                }
            }

            return Json(new { success = true, message = "回覆成功" });
        }
        #endregion
    }
}
