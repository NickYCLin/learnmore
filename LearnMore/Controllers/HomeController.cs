using LearnMore.Models;
using LearnMore.Repository;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Data.SqlClient;
using System.Diagnostics;

namespace LearnMore.Controllers
{
    public class HomeController : Controller
    {
        #region �򥻰Ѽ�
        private readonly ILogger<HomeController> _logger;
        private readonly string _connectionString;
        private readonly IMemoryCache _cache;

        public HomeController(ILogger<HomeController> logger, IConfiguration configuration, IMemoryCache cache)
        {
            _logger = logger;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _cache = cache;
        }
        #endregion

        #region ����
        public async Task<IActionResult> Index(string type = "all", int? groupId = null, string? groupUid = null, string? performer = null, int page = 1)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (!string.IsNullOrEmpty(userEmail))
            {
                string isFirstLoginQuery = @"SELECT IsFirstLogin FROM [Users] WHERE Email = @Email";
                bool isFirstLogin = false;

                try
                {
                    using (var conn = new SqlConnection(_connectionString))
                    {
                        await conn.OpenAsync();
                        using (var cmd = new SqlCommand(isFirstLoginQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@Email", userEmail);

                            var result = await cmd.ExecuteScalarAsync();
                            if (result != null && result != DBNull.Value)
                            {
                                isFirstLogin = Convert.ToBoolean(result);
                            }
                        }
                    }

                    if (isFirstLogin)
                    {
                        return RedirectToAction("Profile", "User");
                    }
                }
                catch (SqlException)
                {
                    return View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
                }
            }

            // ?? �z�L GroupUid �ഫ�� groupId�]�Y�ϥ� /GroupUid/{groupUid}�^
            if (!groupId.HasValue && !string.IsNullOrWhiteSpace(groupUid))
            {
                try
                {
                    using var conn = new SqlConnection(_connectionString);
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand("SELECT GroupId FROM SongGroup WHERE GroupUid = @GroupUid", conn);
                    cmd.Parameters.AddWithValue("@GroupUid", groupUid);
                    var obj = await cmd.ExecuteScalarAsync();
                    if (obj != null && obj != DBNull.Value)
                    {
                        groupId = Convert.ToInt32(obj);
                    }
                }
                catch (SqlException)
                {
                    // ���Ѯɩ����A���@�@��������
                    groupId = null;
                }
            }

            List<Songs> songs = new List<Songs>();
            const int homeAllPageSize = 72;
            int currentPage = Math.Max(1, page);
            int? totalSongs = null;
            bool shouldPageHomeAll = !groupId.HasValue
                && string.Equals(type, "all", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(performer);
            ViewBag.Type = type;
            ViewBag.GroupId = groupId; // �O�d�즳�����޿�
            ViewBag.GroupUid = groupUid; // �ѫe�ݻݭn�i��
            ViewBag.SelectedPerformer = performer?.Trim();
            ViewBag.CurrentPage = currentPage;
            ViewBag.PageSize = homeAllPageSize;

            string query = "";
            List<SqlParameter> parameters = new List<SqlParameter>();

            if (groupId.HasValue)
            {
                query = @"
SELECT S.[SongID], S.[Title], S.[Artist],
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN BaseSongs.[Performer] ELSE NULL END AS [Performer],
S.[YouTubeVideoUrl], S.[ChannelThumbnailUrl], S.[SongUid]
FROM [SongGroupMapping] GM
INNER JOIN [V_SongsData] S ON GM.SongUid = S.SongUid
LEFT JOIN [Songs] BaseSongs ON BaseSongs.SongUid = S.SongUid
WHERE GM.GroupId = @GroupId
ORDER BY S.ViewCount DESC";

                parameters.Add(new SqlParameter("@GroupId", groupId.Value));
            }
            else
            {
                query = @"SELECT ";

                if (type == "weekly" || type == "monthly")
                {
                    query += "TOP 30 ";
                }
                else if (type == "new")
                {
                    query += "TOP 50 ";
                }

                if (type == "new")
                {
                    query += @"[SongID], [Title], [Artist],
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN [Performer] ELSE NULL END AS [Performer],
[YouTubeVideoUrl], [ChannelThumbnailUrl], [SongUid],
CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NOT NULL THEN [HighAccuracyStatus] ELSE NULL END AS [HighAccuracyStatus],
CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NOT NULL THEN [HighAccuracyStatusReason] ELSE NULL END AS [HighAccuracyStatusReason]
FROM [Songs] ";
                }
                else
                {
                    query += @"S.[SongID], S.[Title], S.[Artist],
CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN BaseSongs.[Performer] ELSE NULL END AS [Performer],
S.[YouTubeVideoUrl], S.[ChannelThumbnailUrl], S.[SongUid] FROM [V_SongsData] S
LEFT JOIN [Songs] BaseSongs ON BaseSongs.SongUid = S.SongUid ";
                }

                switch (type)
                {
                    case "weekly":
                        query += "ORDER BY ViewCount_WeeklyGrowth DESC";
                        break;
                    case "monthly":
                        query += "ORDER BY ViewCount_MonthlyGrowth DESC";
                        break;
                    case "new":
                        query += "WHERE AddedDate >= DATEADD(DAY, -7, GETDATE()) ORDER BY SongID DESC";
                        break;
                    default:
                        query += shouldPageHomeAll
                            ? "ORDER BY ViewCount DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY"
                            : "ORDER BY ViewCount DESC";
                        if (shouldPageHomeAll)
                        {
                            parameters.Add(new SqlParameter("@Offset", (currentPage - 1) * homeAllPageSize));
                            parameters.Add(new SqlParameter("@PageSize", homeAllPageSize));
                        }
                        break;
                }
            }

            try
            {
                string homeSongsCacheKey = $"home:songs:{type.ToLowerInvariant()}:{currentPage}";
                bool canUseHomeSongsCache = !groupId.HasValue
                    && string.IsNullOrWhiteSpace(performer)
                    && (string.Equals(type, "all", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "weekly", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "monthly", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type, "new", StringComparison.OrdinalIgnoreCase));

                if (canUseHomeSongsCache && _cache.TryGetValue(homeSongsCacheKey, out List<Songs>? cachedSongs))
                {
                    songs = cachedSongs ?? new List<Songs>();
                    totalSongs = await GetCachedHomeSongsCountAsync();
                    if (shouldPageHomeAll && totalSongs.HasValue)
                    {
                        int totalPages = Math.Max(1, (int)Math.Ceiling(totalSongs.Value / (double)homeAllPageSize));
                        ViewBag.TotalPages = totalPages;
                        if (currentPage > totalPages)
                        {
                            return RedirectToAction(nameof(Index), new { type, page = totalPages });
                        }
                    }
                }
                else using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.OpenAsync();
                    totalSongs = await GetCachedHomeSongsCountAsync(conn);
                    if (shouldPageHomeAll && totalSongs.HasValue)
                    {
                        int totalPages = Math.Max(1, (int)Math.Ceiling(totalSongs.Value / (double)homeAllPageSize));
                        ViewBag.TotalPages = totalPages;
                        if (currentPage > totalPages)
                        {
                            return RedirectToAction(nameof(Index), new { type, page = totalPages });
                        }
                    }

                    using (var cmd = new SqlCommand(query, conn))
                    {
                        if (parameters.Count > 0)
                        {
                            cmd.Parameters.AddRange(parameters.ToArray());
                        }

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                Songs song = new Songs
                                {
                                    SongID = reader.GetInt32(reader.GetOrdinal("SongID")),
                                    Title = reader.GetString(reader.GetOrdinal("Title")),
                                    Artist = reader.GetString(reader.GetOrdinal("Artist")),
                                    Performer = HasColumn(reader, "Performer") && !reader.IsDBNull(reader.GetOrdinal("Performer"))
                                        ? reader.GetString(reader.GetOrdinal("Performer"))
                                        : null,
                                    YouTubeVideoUrl = reader.IsDBNull(reader.GetOrdinal("YouTubeVideoUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("YouTubeVideoUrl")),
                                    ChannelThumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ChannelThumbnailUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("ChannelThumbnailUrl")),
                                    SongUid = reader.IsDBNull(reader.GetOrdinal("SongUid")) ? string.Empty : reader.GetString(reader.GetOrdinal("SongUid")),
                                    HighAccuracyStatus = HasColumn(reader, "HighAccuracyStatus") && !reader.IsDBNull(reader.GetOrdinal("HighAccuracyStatus"))
                                        ? reader.GetString(reader.GetOrdinal("HighAccuracyStatus"))
                                        : null,
                                    HighAccuracyStatusReason = HasColumn(reader, "HighAccuracyStatusReason") && !reader.IsDBNull(reader.GetOrdinal("HighAccuracyStatusReason"))
                                        ? reader.GetString(reader.GetOrdinal("HighAccuracyStatusReason"))
                                        : null
                                };
                                songs.Add(song);
                            }
                        }
                    }

                    if (canUseHomeSongsCache)
                    {
                        _cache.Set(
                            homeSongsCacheKey,
                            songs,
                            new MemoryCacheEntryOptions
                            {
                                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2),
                                SlidingExpiration = TimeSpan.FromSeconds(45)
                            });
                    }
                }
            }
            catch (SqlException)
            {
                return View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }

            if (string.Equals(type, "all", StringComparison.OrdinalIgnoreCase) && !groupId.HasValue)
            {
                ViewBag.PerformerCollections = await GetCachedPerformerCollectionsAsync();
            }
            else
            {
                ViewBag.PerformerCollections = new List<PerformerCollectionViewModel>();
            }

            if (!string.IsNullOrWhiteSpace(performer) && string.Equals(type, "all", StringComparison.OrdinalIgnoreCase) && !groupId.HasValue)
            {
                var selectedPerformer = PerformerNameNormalizer.NormalizeForCollection(performer);
                songs = songs
                    .Where(song => string.Equals(PerformerNameNormalizer.NormalizeForCollection(song.Performer), selectedPerformer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            ViewBag.TotalSongs = totalSongs ?? songs.Count;
            ViewBag.TotalPages = shouldPageHomeAll && totalSongs.HasValue
                ? Math.Max(1, (int)Math.Ceiling(totalSongs.Value / (double)homeAllPageSize))
                : 1;
            ViewBag.ShowHomePagination = shouldPageHomeAll && totalSongs.HasValue && totalSongs.Value > homeAllPageSize;
            ViewBag.HasPreviousSongs = shouldPageHomeAll && currentPage > 1;
            ViewBag.HasMoreSongs = shouldPageHomeAll && totalSongs.HasValue && currentPage * homeAllPageSize < totalSongs.Value;

            if (!string.IsNullOrEmpty(HttpContext.Session.GetString("UserId")))
            {
                var repository = new SongGroupRepository(_connectionString);
                var userId = HttpContext.Session.GetString("UserId");
                ViewBag.Groups = string.IsNullOrEmpty(userId)
                    ? new List<SongGroup>()
                    : repository.GetGroups(userId);
            }

            return View(songs);
        }

        // ?? �s�W�H GroupUid ���|��������s�պq���G /GroupUid/{groupUid}
        [HttpGet("/GroupUid/{groupUid}")]
        public async Task<IActionResult> GroupByUid(string groupUid)
        {
            return await Index("all", null, groupUid);
        }
        #endregion

        #region �q���j�M
        public async Task<IActionResult> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return RedirectToAction("Index");
            }

            List<Songs> songs = new List<Songs>();
            string trimmedQuery = query.Trim();

            string sqlQuery = @"
WITH AliasMatches AS (
    SELECT A.SongUid, A.AliasText, A.AliasType,
           ROW_NUMBER() OVER (
               PARTITION BY A.SongUid
               ORDER BY
                 CASE
                   WHEN A.AliasText = @ExactQuery THEN 0
                   WHEN A.AliasText LIKE @PrefixQuery THEN 1
                   ELSE 2
                 END,
                 A.AliasText
           ) AS RowNumber
    FROM [SongAliases] A
    WHERE A.AliasText LIKE @LikeQuery
       OR (
            A.AliasType = N'romanized_title'
            AND @CompactRomanLikeQuery IS NOT NULL
            AND LOWER(
                REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                    A.AliasText,
                    N' ', N''), N'-', N''), N'''', N''), N'’', N''), N'.', N''), N',', N''),
                    N'ā', N'a'), N'ī', N'i'), N'ū', N'u'), N'ē', N'e'), N'ō', N'o')
            ) LIKE @CompactRomanLikeQuery
       )
)
SELECT S.[SongID], S.[Title], S.[Artist],
       CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN S.[Performer] ELSE NULL END AS [Performer],
       S.[Translator], S.[YouTubeVideoUrl], S.[SongUid]
FROM [Songs] S
LEFT JOIN AliasMatches AliasHit ON AliasHit.SongUid = S.SongUid AND AliasHit.RowNumber = 1
WHERE S.[Title] LIKE @LikeQuery
   OR S.[Artist] LIKE @LikeQuery
   OR S.[Translator] LIKE @LikeQuery
   OR (COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL AND S.[Performer] LIKE @LikeQuery)
   OR AliasHit.AliasText IS NOT NULL
ORDER BY
  CASE
    WHEN S.[Title] = @ExactQuery THEN 0
    WHEN S.[Title] LIKE @PrefixQuery THEN 1
    WHEN AliasHit.AliasText = @ExactQuery THEN 2
    WHEN AliasHit.AliasText LIKE @PrefixQuery THEN 3
    WHEN S.[Artist] LIKE @LikeQuery THEN 4
    WHEN S.[Title] LIKE @LikeQuery THEN 5
    WHEN AliasHit.AliasText IS NOT NULL THEN 6
    WHEN S.[Translator] LIKE @LikeQuery THEN 7
    ELSE 8
  END,
  S.[SongID] DESC";

            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    await conn.OpenAsync();
                    await EnsureSongAliasesTableAsync(conn);
                    ViewBag.TotalSongs = await GetCachedHomeSongsCountAsync(conn);

                    using (SqlCommand cmd = new SqlCommand(sqlQuery, conn))
                    {
                        AddSearchParameters(cmd, trimmedQuery);

                        using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                Songs song = new Songs
                                {
                                    SongID = reader.GetInt32(reader.GetOrdinal("SongID")),
                                    Title = reader.IsDBNull(reader.GetOrdinal("Title")) ? string.Empty : reader.GetString(reader.GetOrdinal("Title")),
                                    Artist = reader.IsDBNull(reader.GetOrdinal("Artist")) ? string.Empty : reader.GetString(reader.GetOrdinal("Artist")),
                                    Performer = reader.IsDBNull(reader.GetOrdinal("Performer")) ? null : reader.GetString(reader.GetOrdinal("Performer")),
                                    Translator = reader.IsDBNull(reader.GetOrdinal("Translator")) ? null : reader.GetString(reader.GetOrdinal("Translator")),
                                    YouTubeVideoUrl = reader.IsDBNull(reader.GetOrdinal("YouTubeVideoUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("YouTubeVideoUrl")),
                                    SongUid = reader.IsDBNull(reader.GetOrdinal("SongUid")) ? string.Empty : reader.GetString(reader.GetOrdinal("SongUid"))
                                };
                                songs.Add(song);
                            }
                        }
                    }
                }
            }
            catch (SqlException)
            {
                return View("Error", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }

            return View("Index", songs);
        }

        [HttpGet("api/search/suggestions")]
        public async Task<IActionResult> SearchSuggestions(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 1)
            {
                return Json(Array.Empty<object>());
            }

            string trimmedQuery = q.Trim();
            var suggestions = new List<object>();

            const string sqlQuery = @"
WITH AliasMatches AS (
    SELECT A.SongUid, A.AliasText, A.AliasType,
           ROW_NUMBER() OVER (
               PARTITION BY A.SongUid
               ORDER BY
                 CASE
                   WHEN A.AliasText = @ExactQuery THEN 0
                   WHEN A.AliasText LIKE @PrefixQuery THEN 1
                   ELSE 2
                 END,
                 A.AliasText
           ) AS RowNumber
    FROM [SongAliases] A
    WHERE A.AliasText LIKE @LikeQuery
       OR (
            A.AliasType = N'romanized_title'
            AND @CompactRomanLikeQuery IS NOT NULL
            AND LOWER(
                REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                    A.AliasText,
                    N' ', N''), N'-', N''), N'''', N''), N'’', N''), N'.', N''), N',', N''),
                    N'ā', N'a'), N'ī', N'i'), N'ū', N'u'), N'ē', N'e'), N'ō', N'o')
            ) LIKE @CompactRomanLikeQuery
       )
)
SELECT
    S.SongUid,
    S.Title,
    S.Artist,
    S.SongID,
    CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN S.Performer ELSE NULL END AS Performer,
    S.YouTubeVideoUrl,
    MatchRank = CASE
        WHEN S.Title = @ExactQuery THEN 0
        WHEN S.Title LIKE @PrefixQuery THEN 1
        WHEN AliasHit.AliasText = @ExactQuery THEN 2
        WHEN AliasHit.AliasText LIKE @PrefixQuery THEN 3
        WHEN S.Artist LIKE @LikeQuery THEN 4
        WHEN S.Title LIKE @LikeQuery THEN 5
        WHEN AliasHit.AliasText IS NOT NULL THEN 6
        WHEN S.Translator LIKE @LikeQuery THEN 7
        WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL AND S.Performer LIKE @LikeQuery THEN 8
        ELSE 9
    END,
    matchedBy = CASE
        WHEN AliasHit.AliasText IS NOT NULL THEN AliasHit.AliasType
        WHEN S.Title LIKE @LikeQuery THEN N'title'
        WHEN S.Artist LIKE @LikeQuery THEN N'artist'
        WHEN S.Translator LIKE @LikeQuery THEN N'translator'
        WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL AND S.Performer LIKE @LikeQuery THEN N'performer'
        ELSE N'song'
    END,
    matchedText = COALESCE(AliasHit.AliasText,
        CASE
            WHEN S.Title LIKE @LikeQuery THEN S.Title
            WHEN S.Artist LIKE @LikeQuery THEN S.Artist
            WHEN S.Translator LIKE @LikeQuery THEN S.Translator
            WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL AND S.Performer LIKE @LikeQuery THEN S.Performer
            ELSE S.Title
        END)
FROM [Songs] S
LEFT JOIN AliasMatches AliasHit ON AliasHit.SongUid = S.SongUid AND AliasHit.RowNumber = 1
WHERE S.Title LIKE @LikeQuery
   OR S.Artist LIKE @LikeQuery
   OR S.Translator LIKE @LikeQuery
   OR (COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL AND S.Performer LIKE @LikeQuery)
   OR AliasHit.AliasText IS NOT NULL
ORDER BY MatchRank, SongID DESC";

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                await EnsureSongAliasesTableAsync(conn);

                using var cmd = new SqlCommand(sqlQuery, conn);
                AddSearchParameters(cmd, trimmedQuery);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string songUid = reader.IsDBNull(reader.GetOrdinal("SongUid")) ? string.Empty : reader.GetString(reader.GetOrdinal("SongUid"));
                    string youTubeVideoUrl = reader.IsDBNull(reader.GetOrdinal("YouTubeVideoUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("YouTubeVideoUrl"));
                    if (string.IsNullOrWhiteSpace(songUid))
                    {
                        continue;
                    }

                    suggestions.Add(new
                    {
                        songUid,
                        title = reader.IsDBNull(reader.GetOrdinal("Title")) ? string.Empty : reader.GetString(reader.GetOrdinal("Title")),
                        artist = reader.IsDBNull(reader.GetOrdinal("Artist")) ? string.Empty : reader.GetString(reader.GetOrdinal("Artist")),
                        performer = reader.IsDBNull(reader.GetOrdinal("Performer")) ? null : reader.GetString(reader.GetOrdinal("Performer")),
                        thumbnailUrl = BuildYouTubeThumbnailUrl(youTubeVideoUrl),
                        matchedBy = reader.IsDBNull(reader.GetOrdinal("matchedBy")) ? "song" : reader.GetString(reader.GetOrdinal("matchedBy")),
                        matchedText = reader.IsDBNull(reader.GetOrdinal("matchedText")) ? string.Empty : reader.GetString(reader.GetOrdinal("matchedText")),
                        url = Url.Content($"~/Lyrics/{songUid}")
                    });
                }
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Search suggestions failed for query {Query}", trimmedQuery);
                return Json(Array.Empty<object>());
            }

            return Json(suggestions);
        }

        private static void AddSearchParameters(SqlCommand cmd, string query)
        {
            cmd.Parameters.AddWithValue("@ExactQuery", query);
            cmd.Parameters.AddWithValue("@PrefixQuery", query + "%");
            cmd.Parameters.AddWithValue("@LikeQuery", "%" + query + "%");
            var compactRomanQuery = NormalizeCompactRomanSearchQuery(query);
            cmd.Parameters.AddWithValue(
                "@CompactRomanLikeQuery",
                string.IsNullOrWhiteSpace(compactRomanQuery) ? (object)DBNull.Value : "%" + compactRomanQuery + "%");
        }

        private static string NormalizeCompactRomanSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(query.Length);
            foreach (var ch in query.Trim().ToLowerInvariant())
            {
                if (ch is ' ' or '-' or '\'' or '’' or '.' or ',')
                {
                    continue;
                }

                if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9')
                {
                    builder.Append(ch);
                    continue;
                }

                var normalized = ch switch
                {
                    'ā' => 'a',
                    'ī' => 'i',
                    'ū' => 'u',
                    'ē' => 'e',
                    'ō' => 'o',
                    _ => (char?)null
                };

                if (normalized.HasValue)
                {
                    builder.Append(normalized.Value);
                }
            }

            return builder.ToString();
        }

        private static string? BuildYouTubeThumbnailUrl(string? youTubeVideoUrl)
        {
            string? videoId = ExtractYouTubeVideoId(youTubeVideoUrl);
            return string.IsNullOrWhiteSpace(videoId)
                ? null
                : $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg";
        }

        private static string? ExtractYouTubeVideoId(string? youTubeVideoUrl)
        {
            if (string.IsNullOrWhiteSpace(youTubeVideoUrl))
            {
                return null;
            }

            try
            {
                var uri = new Uri(youTubeVideoUrl);
                if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                {
                    string id = uri.AbsolutePath.Trim('/');
                    return id.Length >= 11 ? id[..11] : null;
                }

                if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string? id = query["v"];
                    return !string.IsNullOrWhiteSpace(id) && id.Length >= 11 ? id[..11] : null;
                }
            }
            catch (UriFormatException)
            {
                return null;
            }

            return null;
        }

        private static async Task EnsureSongAliasesTableAsync(SqlConnection conn)
        {
            const string sql = @"
IF OBJECT_ID('dbo.SongAliases', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SongAliases] (
        [AliasID] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SongUid] NVARCHAR(50) NOT NULL,
        [AliasText] NVARCHAR(255) NOT NULL,
        [AliasType] NVARCHAR(50) NOT NULL,
        [Source] NVARCHAR(100) NULL,
        [CreatedAt] DATETIME NOT NULL CONSTRAINT [DF_SongAliases_CreatedAt] DEFAULT GETDATE()
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX [IX_SongAliases_AliasText] ON [dbo].[SongAliases] ([AliasText]) INCLUDE ([SongUid], [AliasType]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_SongUid' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX [IX_SongAliases_SongUid] ON [dbo].[SongAliases] ([SongUid]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SongAliases_SongUid_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    ;WITH DuplicateAliases AS (
        SELECT [AliasID],
               ROW_NUMBER() OVER (PARTITION BY [SongUid], [AliasText] ORDER BY [AliasID]) AS [RowNumber]
        FROM [dbo].[SongAliases]
    )
    DELETE FROM DuplicateAliases WHERE [RowNumber] > 1;

    CREATE UNIQUE INDEX [UX_SongAliases_SongUid_AliasText]
    ON [dbo].[SongAliases] ([SongUid], [AliasText])
    WITH (IGNORE_DUP_KEY = ON);
END;";

            using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        #endregion

        public IActionResult Privacy()
        {
            return RedirectToAction("About", "User");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        private static List<PerformerCollectionViewModel> BuildPerformerCollections(IEnumerable<Songs> songs)
        {
            return songs
                .Where(song => !string.IsNullOrWhiteSpace(song.Performer))
                .GroupBy(song => PerformerNameNormalizer.NormalizeForCollection(song.Performer), StringComparer.OrdinalIgnoreCase)
                .Select(group => new PerformerCollectionViewModel
                {
                    Performer = group.Key,
                    SongCount = group.Count(),
                    ThumbnailUrl = SelectPerformerThumbnailUrl(group),
                    SampleTitle = group.FirstOrDefault()?.Title
                })
                .Where(collection => collection.SongCount > 5)
                .OrderByDescending(collection => collection.SongCount)
                .ThenBy(collection => collection.Performer, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        private static string? SelectPerformerThumbnailUrl(IEnumerable<Songs> songs)
        {
            var songList = songs.ToList();
            string? channelThumbnailUrl = songList
                .Select(song => song.ChannelThumbnailUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .GroupBy(url => url, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .FirstOrDefault();

            return !string.IsNullOrWhiteSpace(channelThumbnailUrl)
                ? channelThumbnailUrl
                : BuildYouTubeThumbnailUrl(songList.FirstOrDefault(song => !string.IsNullOrWhiteSpace(song.YouTubeVideoUrl))?.YouTubeVideoUrl);
        }

        private static async Task<int> CountHomeSongsAsync(SqlConnection conn)
        {
            using var cmd = new SqlCommand("SELECT COUNT(1) FROM [V_SongsData]", conn);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private async Task<int> GetCachedHomeSongsCountAsync()
        {
            return await _cache.GetOrCreateAsync("home:songs:count", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();
                return await CountHomeSongsAsync(conn);
            });
        }

        private async Task<int> GetCachedHomeSongsCountAsync(SqlConnection conn)
        {
            return await _cache.GetOrCreateAsync("home:songs:count", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await CountHomeSongsAsync(conn);
            });
        }

        private async Task<List<PerformerCollectionViewModel>> GetCachedPerformerCollectionsAsync()
        {
            return await _cache.GetOrCreateAsync("home:performer-collections", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                entry.SlidingExpiration = TimeSpan.FromMinutes(2);
                return BuildPerformerCollections(await LoadPerformerCollectionSongsAsync());
            }) ?? new List<PerformerCollectionViewModel>();
        }

        private async Task<List<Songs>> LoadPerformerCollectionSongsAsync()
        {
            var songs = new List<Songs>();
            const string query = @"
SELECT
    CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN [Performer] ELSE NULL END AS [Performer],
    [YouTubeVideoUrl],
    [ChannelThumbnailUrl]
FROM [Songs]
WHERE CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN [Performer] ELSE NULL END IS NOT NULL";

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(query, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                songs.Add(new Songs
                {
                    Performer = reader.IsDBNull(reader.GetOrdinal("Performer"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("Performer")),
                    YouTubeVideoUrl = reader.IsDBNull(reader.GetOrdinal("YouTubeVideoUrl"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("YouTubeVideoUrl")),
                    ChannelThumbnailUrl = reader.IsDBNull(reader.GetOrdinal("ChannelThumbnailUrl"))
                        ? string.Empty
                        : reader.GetString(reader.GetOrdinal("ChannelThumbnailUrl"))
                });
            }

            return songs;
        }

        private static bool HasColumn(SqlDataReader reader, string columnName)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
