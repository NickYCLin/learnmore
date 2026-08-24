using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;
using System.Data;
using System.Globalization;

namespace LearnMore.Controllers
{
    [AllowAnonymous]
    [Route("GroupPlayer")]
    public class GroupPlayerController : Controller
    {
        private static readonly System.Text.RegularExpressions.Regex SafeSongUidPattern = new("^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly string _connectionString;

        public GroupPlayerController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }

        // 🆕 修改為使用 GroupUid（Route Segment 版）
        [HttpGet("Play/{groupUid}")]
        public IActionResult Play(string groupUid)
        {
            return PlayCore(groupUid);
        }

        // 演唱者自動合輯播放：/GroupPlayer/Performer?performer=...
        [HttpGet("Performer")]
        public IActionResult PlayPerformer([FromQuery] string performer)
        {
            if (string.IsNullOrWhiteSpace(performer))
            {
                return RedirectToAction("Index", "Home", new { type = "all" });
            }

            var trimmedPerformer = performer.Trim();
            var canonicalPerformer = PerformerNameNormalizer.NormalizeForCollection(trimmedPerformer);
            var performerAliases = PerformerNameNormalizer.GetCollectionAliases(canonicalPerformer);
            var email = HttpContext.Session.GetString("Email");
            var model = new GroupPlayerViewModel
            {
                GroupUid = "performer:" + canonicalPerformer,
                GroupName = canonicalPerformer + " 合輯",
                UserEmail = email
            };

            var aliasConditions = performerAliases
                .Select((_, index) => $"LTRIM(RTRIM(s.Performer)) = @Performer{index}")
                .ToArray();
            string sql = $@"SELECT s.SongUid, s.Title, s.Artist, s.YouTubeVideoUrl,
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN s.Performer ELSE NULL END AS Performer
FROM Songs s
WHERE {string.Join(" OR ", aliasConditions)}
ORDER BY s.SongID DESC";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                for (int i = 0; i < performerAliases.Count; i++)
                {
                    cmd.Parameters.AddWithValue($"@Performer{i}", performerAliases[i]);
                }
                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var songUid = reader.IsDBNull(0) ? null : reader.GetString(0);
                        if (string.IsNullOrWhiteSpace(songUid)) continue;

                        model.Songs.Add(new SongItem
                        {
                            SongUid = songUid,
                            Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            Artist = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            YouTubeVideoUrl = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            Performer = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                        });
                    }
                }
            }

            return View("Play", model);
        }

        // 🆕 新增 QueryString 版，避免特殊字元問題：/GroupPlayer/Play?groupUid=xxx
        [HttpGet("Play")]
        public IActionResult PlayByQuery([FromQuery] string groupUid)
        {
            return PlayCore(groupUid);
        }

        private IActionResult PlayCore(string groupUid)
        {
            var email = HttpContext.Session.GetString("Email");
            var model = new GroupPlayerViewModel { GroupUid = groupUid, UserEmail = email };

            const string sql = @"SELECT g.GroupName, s.SongUid, s.Title, s.Artist, s.YouTubeVideoUrl,
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN s.Performer ELSE NULL END AS Performer
FROM SongGroupMapping m
INNER JOIN SongGroup g ON m.GroupId = g.GroupId
LEFT JOIN Songs s ON s.SongUid = m.SongUid
WHERE g.GroupUid = @GroupUid";

            using (var conn = new SqlConnection(_connectionString))
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@GroupUid", groupUid);
                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    bool firstRow = true;
                    while (reader.Read())
                    {
                        if (firstRow)
                        {
                            model.GroupName = reader.IsDBNull(0) ? "未命名群組" : reader.GetString(0);
                            firstRow = false;
                        }

                        var songUid = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(songUid)) continue;

                        var title = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var artist = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        var youTube = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        var performer = reader.IsDBNull(5) ? "" : reader.GetString(5);

                        model.Songs.Add(new SongItem
                        {
                            SongUid = songUid,
                            Title = title,
                            Artist = artist,
                            YouTubeVideoUrl = youTube,
                            Performer = performer
                        });
                    }
                }
            }

            // 直接使用 Play.cshtml
            return View("Play", model);
        }

        // 新增：取得單首歌的資料、歌詞（從動態表 Songs_{songUid}）與留言（路由版）
        [HttpGet("SongDetail/{songUid}")]
        public IActionResult SongDetail(string songUid)
        {
            return SongDetailCore(songUid);
        }

        // 新增：同功能（QueryString 版），避免 route segment 對特殊字元的限制
        [HttpGet("SongDetail")]
        public IActionResult SongDetailByQuery([FromQuery] string songUid)
        {
            return SongDetailCore(songUid);
        }

        private IActionResult SongDetailCore(string songUid)
        {
            if (string.IsNullOrWhiteSpace(songUid)) return Json(new { success = false, message = "songUid required" });
            if (!SafeSongUidPattern.IsMatch(songUid)) return BadRequest(new { success = false, message = "invalid songUid" });

            string appBasePath = HttpContext.Request.PathBase;
            var email = HttpContext.Session.GetString("Email");
            bool isManage = false;
            bool isEnableRoman = true;
            bool isEnableAuto = true;

            if (!string.IsNullOrWhiteSpace(email))
            {
                using (SqlConnection connUser = new SqlConnection(_connectionString))
                {
                    connUser.Open();
                    string queryUser = "SELECT Manager, EnableRoman, EnableAuto FROM Users WHERE Email = @UserEmail";
                    using (SqlCommand cmdUser = new SqlCommand(queryUser, connUser))
                    {
                        cmdUser.Parameters.Add("@UserEmail", SqlDbType.NVarChar, 500).Value = email;
                        using (SqlDataReader readerUser = cmdUser.ExecuteReader())
                        {
                            if (readerUser.Read())
                            {
                                isManage = !readerUser.IsDBNull(0) && readerUser.GetBoolean(0);
                                isEnableRoman = !readerUser.IsDBNull(1) && readerUser.GetBoolean(1);
                                isEnableAuto = !readerUser.IsDBNull(2) && readerUser.GetBoolean(2);
                            }
                        }
                    }
                }
            }

            Songs? song = null;
            var lyricsList = new List<Lyrics>();
            var comments = new List<object>();

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                // 1) 取得 Songs 表的基本資訊
                string songQuery = @"SELECT Title, Artist,
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN Performer ELSE NULL END AS Performer,
Translator, TranslationSource, YouTubeVideoUrl, SongUid FROM Songs WHERE SongUid = @SongUid";
                using (var cmd = new SqlCommand(songQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@SongUid", songUid);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            song = new Songs
                            {
                                Title = reader["Title"].ToString() ?? string.Empty,
                                Artist = reader["Artist"].ToString() ?? string.Empty,
                                Performer = reader["Performer"].ToString(),
                                Translator = reader["Translator"].ToString(),
                                TranslationSource = reader["TranslationSource"].ToString(),
                                YouTubeVideoUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
                                SongUid = reader["SongUid"].ToString() ?? string.Empty
                            };
                        }
                    }
                }

                if (song != null)
                {
                    song.InstrumentalAudioUrl = GetAudioStemUrl(songUid, "instrumental");
                    song.VocalsAudioUrl = GetAudioStemUrl(songUid, "vocals");
                }

                // 2) 取得歌詞（動態表）
                try
                {
                    string dynamicTableName = $"[Songs_{songUid}]";
                    string getLyricsQuery = $"SELECT LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman FROM {dynamicTableName} ORDER BY TimeStamp";
                    using (var cmd = new SqlCommand(getLyricsQuery, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var timeStampText = reader["TimeStamp"].ToString();
                            var l = new Lyrics
                            {
                                LyricID = Convert.ToInt32(reader["LyricID"]),
                                TimeStamp = float.TryParse(timeStampText, out var parsedTimeStamp) ? parsedTimeStamp : 0f,
                                Japanese = reader["JapaneseRuby"].ToString() ?? string.Empty,
                                Chinese = reader["Chinese"].ToString() ?? string.Empty
                            };
                            if (!reader.IsDBNull(reader.GetOrdinal("Roman"))) l.Roman = reader["Roman"].ToString() ?? string.Empty;
                            lyricsList.Add(l);
                        }
                    }
                }
                catch
                {
                    lyricsList = new List<Lyrics>();
                }

                // 3) 取得留言（從 V_Comments）
                string commentsQuery = @"SELECT [CommentId],[SongUid],[UserEmail],[UserName],[Message],[TimeStamp],[IsPrivate],[Avatar]
FROM [V_Comments]
WHERE [SongUid] = @SongUid
ORDER BY [TimeStamp] DESC";

                using (var cmd = new SqlCommand(commentsQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@SongUid", songUid);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string? avatar = reader["Avatar"] != DBNull.Value ? reader["Avatar"].ToString() : null;
                            string userName = reader.IsDBNull(3) ? "訪客" : reader.GetString(3);

                            string commentEmail = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                            bool canViewPrivate = isManage || (!string.IsNullOrWhiteSpace(email) && string.Equals(commentEmail, email, StringComparison.OrdinalIgnoreCase));
                            bool isPrivate = !reader.IsDBNull(6) && reader.GetBoolean(6);

                            comments.Add(new
                            {
                                CommentId = reader.GetGuid(0),
                                SongUid = reader.GetString(1),
                                UserName = userName,
                                Message = isPrivate && !canViewPrivate ? string.Empty : reader.GetString(4),
                                TimeStamp = reader.GetDateTime(5),
                                IsPrivate = isPrivate,
                                CanViewPrivate = canViewPrivate,
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

            return Json(new { success = true, song, lyrics = lyricsList, comments, isLoggedIn = !string.IsNullOrWhiteSpace(email), isManage, isEnableRoman, isEnableAuto });
        }

        private string? GetAudioStemUrl(string songUid, string stemKind)
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

                return NormalizePublicAudioUrl(AppendAudioStemVersion(publicUrl, updatedAt));
            }
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

            return Url.Content(publicUrl);
        }
    }
}
