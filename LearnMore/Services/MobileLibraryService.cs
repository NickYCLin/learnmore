using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LearnMore.Services;

public sealed class MobileLibraryService(IConfiguration configuration)
{
    public record Song(
        [property: JsonPropertyName("songUid")] string SongUid,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] string Artist,
        [property: JsonPropertyName("performer")] string Performer,
        [property: JsonPropertyName("videoId")] string? VideoId);
    public record Line(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("time")] double Time,
        [property: JsonPropertyName("japanese")] string Japanese,
        [property: JsonPropertyName("ruby")] string Ruby,
        [property: JsonPropertyName("roman")] string Roman,
        [property: JsonPropertyName("chinese")] string Chinese);
    public record Detail(
        [property: JsonPropertyName("song")] Song Song,
        [property: JsonPropertyName("lyrics")] IReadOnlyList<Line> Lyrics);
    public static bool ValidSongUid(string? uid) => uid is not null && Regex.IsMatch(uid, "^[A-Za-z0-9_-]{1,80}$");
    private SqlConnection Connection() => new(configuration.GetConnectionString("DefaultConnection"));

    public async Task<IReadOnlyList<Song>> Search(string query, int page, int? userId, CancellationToken ct)
    {
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        // 公開首頁沿用網頁的熱門歌曲來源與排序；搜尋與收藏保留原有順序。
        var useHomeOrder = string.IsNullOrEmpty(query) && !userId.HasValue;
        var source = useHomeOrder
            ? "V_SongsData V INNER JOIN Songs S ON S.SongUid = V.SongUid"
            : "Songs S";
        var order = useHomeOrder ? "V.ViewCount DESC, S.SongID DESC" : "S.SongID DESC";
        var sql = $"""
            SELECT S.SongUid, S.Title, S.Artist, S.Performer, S.YouTubeVideoUrl
            FROM {source}
            WHERE (@Query = '' OR S.Title LIKE @Pattern ESCAPE '\' OR S.Artist LIKE @Pattern ESCAPE '\' OR S.Performer LIKE @Pattern ESCAPE '\')
              AND (@UserId IS NULL OR EXISTS (
                SELECT 1 FROM SongGroupMapping M JOIN SongGroup G ON G.GroupId = M.GroupId
                WHERE M.SongUid = S.SongUid AND G.UserId = @UserId))
            ORDER BY {order} OFFSET @Offset ROWS FETCH NEXT 30 ROWS ONLY
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Query", SqlDbType.NVarChar, 100).Value = query;
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 205).Value = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * 30;
        var songs = new List<Song>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) songs.Add(ReadSong(reader));
        return songs;
    }

    public async Task<Detail?> Get(string uid, CancellationToken ct)
    {
        if (!ValidSongUid(uid)) return null;
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT SongUid, Title, Artist, Performer, YouTubeVideoUrl FROM Songs WHERE SongUid = @Uid", connection);
        command.Parameters.Add("@Uid", SqlDbType.NVarChar, 80).Value = uid;
        Song song;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            song = ReadSong(reader);
        }
        // uid 經白名單驗證；表名不能使用 SQL 參數。
        command.CommandText = $"IF OBJECT_ID(@Table, 'U') IS NOT NULL SELECT LyricID, TimeStamp, Japanese, JapaneseRuby, Roman, Chinese FROM [dbo].[Songs_{uid}] ORDER BY TimeStamp, LyricID";
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = "dbo.Songs_" + uid;
        var lines = new List<Line>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                double.TryParse(Convert.ToString(reader["TimeStamp"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var time);
                lines.Add(new Line(Convert.ToInt32(reader["LyricID"]), double.IsFinite(time) ? Math.Max(0, time) : 0,
                    Text(reader, "Japanese"), JapaneseRubySanitizer.NormalizeRubyHtml(Text(reader, "JapaneseRuby")), Text(reader, "Roman"), Text(reader, "Chinese")));
            }
        }
        return new Detail(song, lines);
    }

    // 收藏沿用網站的歌曲群組資料；只改動使用者明確選擇的群組。
    public async Task<bool> SetGroupSong(int userId, int groupId, string uid, bool included, CancellationToken ct)
    {
        if (!ValidSongUid(uid)) return false;
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM SongGroup WITH (UPDLOCK) WHERE GroupId=@GroupId AND UserId=@UserId", connection, transaction);
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = groupId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Uid", SqlDbType.NVarChar, 80).Value = uid;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0) return false;
        command.CommandText = "SELECT COUNT(*) FROM Songs WHERE SongUid=@Uid";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 0) return false;
        command.CommandText = included
            ? "IF NOT EXISTS (SELECT 1 FROM SongGroupMapping WHERE GroupId=@GroupId AND SongUid=@Uid) INSERT INTO SongGroupMapping (GroupId,SongUid) VALUES (@GroupId,@Uid)"
            : "DELETE FROM SongGroupMapping WHERE GroupId=@GroupId AND SongUid=@Uid";
        await command.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        return true;
    }

    private static string Text(SqlDataReader reader, string name) => reader[name]?.ToString() ?? "";
    private static Song ReadSong(SqlDataReader reader) => new(Text(reader, "SongUid"), Text(reader, "Title"), Text(reader, "Artist"), Text(reader, "Performer"), YouTubeVideoIdExtractor.Extract(Text(reader, "YouTubeVideoUrl")));
}
