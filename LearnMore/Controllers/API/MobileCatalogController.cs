using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers.API;

// Public, read-only catalog. Uses the same database/view as the website.
[ApiController]
[Route("api/mobile/v1/songs")]
public sealed class MobileCatalogController(IConfiguration configuration) : ControllerBase
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
    private static readonly Regex SafeUid = new("\\A[A-Za-z0-9_-]{1,100}\\z", RegexOptions.CultureInvariant);

    [HttpGet]
    public async Task<IActionResult> List(string? q = null, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > 10000 || pageSize < 1 || pageSize > 100 || q?.Length > 200)
            return BadRequest(new { error = "Invalid search or pagination." });

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT SongUid, Title, Artist, ChannelThumbnailUrl, YouTubeVideoUrl
            FROM V_SongsData
            WHERE (@Query = '' OR CHARINDEX(@Query, Title) > 0 OR CHARINDEX(@Query, Artist) > 0)
            ORDER BY ViewCount DESC, SongID DESC
            OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY
            """, connection);
        command.Parameters.Add("@Query", SqlDbType.NVarChar, 200).Value = q?.Trim() ?? "";
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * pageSize;
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = pageSize + 1;
        var songs = new List<MobileSong>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            songs.Add(new(reader["SongUid"].ToString()!, reader["Title"].ToString()!, reader["Artist"].ToString()!, reader["ChannelThumbnailUrl"].ToString()!, reader["YouTubeVideoUrl"].ToString()!));
        var hasMore = songs.Count > pageSize;
        if (hasMore) songs.RemoveAt(songs.Count - 1);
        return Ok(new MobileSongPage(songs, hasMore));
    }

    [HttpGet("{songUid}/lyrics")]
    public async Task<IActionResult> Lyrics(string songUid, CancellationToken cancellationToken = default)
    {
        // Song UIDs become legacy table identifiers; never interpolate an unchecked value.
        if (!SafeUid.IsMatch(songUid)) return BadRequest(new { error = "Invalid song UID." });
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new SqlCommand("SELECT COUNT(*) FROM V_SongsData WHERE SongUid = @Uid", connection);
        exists.Parameters.Add("@Uid", SqlDbType.NVarChar, 100).Value = songUid;
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return NotFound();

        await using var table = new SqlCommand("SELECT OBJECT_ID(@Table, 'U')", connection);
        table.Parameters.Add("@Table", SqlDbType.NVarChar, 256).Value = $"dbo.Songs_{songUid}";
        var tableId = await table.ExecuteScalarAsync(cancellationToken);
        if (tableId is null or DBNull) return Ok(Array.Empty<MobileLyric>());

        await using var command = new SqlCommand($"SELECT LyricID, TimeStamp, Japanese, Chinese, Roman FROM [dbo].[Songs_{songUid}] ORDER BY TimeStamp, LyricID", connection);
        var lyrics = new List<MobileLyric>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            double.TryParse(Convert.ToString(reader["TimeStamp"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds);
            lyrics.Add(new(Convert.ToInt32(reader["LyricID"]), double.IsFinite(seconds) ? Math.Max(0, seconds) : 0,
                reader["Japanese"].ToString()!, reader["Chinese"].ToString()!, reader["Roman"].ToString()!));
        }
        return Ok(lyrics);
    }
}

// Explicit wire names isolate the mobile contract from MVC's global naming policy.
public sealed record MobileSong(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("thumbnailURL")] string ThumbnailURL,
    [property: JsonPropertyName("videoURL")] string VideoURL = "");
public sealed record MobileSongPage(
    [property: JsonPropertyName("songs")] List<MobileSong> Songs,
    [property: JsonPropertyName("hasMore")] bool HasMore);
public sealed record MobileLyric(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("japanese")] string Japanese,
    [property: JsonPropertyName("chinese")] string Chinese,
    [property: JsonPropertyName("roman")] string Roman);
