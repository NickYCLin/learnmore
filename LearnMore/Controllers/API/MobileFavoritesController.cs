using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.SqlClient;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.Mvc;
using static LearnMore.Services.Mobile.MobileAccountStore;

namespace LearnMore.Controllers.API;

[ApiController]
[Route("api/mobile/v1/favorites")]
[ServiceFilter(typeof(MobileAuthorizeFilter))]
public sealed class MobileFavoritesController(IConfiguration config) : ControllerBase
{
    private string UserId => ((MobileUser)HttpContext.Items[typeof(MobileUser)]!).Id.ToString();
    private SqlConnection Connection() => new(config.GetConnectionString("DefaultConnection") ?? "");
    private static readonly Regex SafeUid = new("\\A[A-Za-z0-9_-]{1,100}\\z", RegexOptions.CultureInvariant);

    [HttpGet("groups")]
    public async Task<IActionResult> Groups(string? songId = null, CancellationToken ct = default)
    {
        if (songId is not null && !SafeUid.IsMatch(songId)) return BadRequest();
        await using var db = Connection(); await db.OpenAsync(ct);
        using var cmd = Command(db, null, """
            SELECT G.GroupId, G.GroupName,
              (SELECT COUNT(*) FROM dbo.SongGroupMapping M WHERE M.GroupId = G.GroupId) AS SongCount,
              CASE WHEN EXISTS (SELECT 1 FROM dbo.SongGroupMapping M WHERE M.GroupId = G.GroupId AND M.SongUid = @Song) THEN 1 ELSE 0 END AS ContainsSong
            FROM dbo.SongGroup G WHERE G.UserId = @User ORDER BY G.CreateTime DESC, G.GroupId DESC
            """, ("@User", UserId), ("@Song", songId ?? ""));
        using var rows = await cmd.ExecuteReaderAsync(ct);
        var groups = new List<MobileGroup>();
        while (await rows.ReadAsync(ct)) groups.Add(new(rows.GetInt32(0), rows.GetString(1), rows.GetInt32(2), rows.GetInt32(3) != 0));
        return Ok(groups);
    }

    [HttpPost("groups")]
    public async Task<IActionResult> Create(GroupNameRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest();
        await using var db = Connection(); await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        using var cmd = Command(db, tx, """
            IF (SELECT COUNT(*) FROM dbo.SongGroup WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @User) >= 200
                SELECT -1;
            ELSE
                INSERT INTO dbo.SongGroup (UserId, GroupName, CreateTime)
                OUTPUT INSERTED.GroupId VALUES (@User, @Name, GETDATE());
            """, ("@User", UserId), ("@Name", request.Name.Trim()));
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await tx.CommitAsync(ct);
        return id < 0 ? Conflict(new { error = "歌單數量已達上限。" }) : Ok(new MobileGroup(id, request.Name.Trim(), 0, false));
    }

    [HttpGet("songs")]
    public async Task<IActionResult> Songs(int? groupId = null, int page = 1, CancellationToken ct = default)
    {
        if (page < 1 || page > 10000 || groupId <= 0) return BadRequest();
        await using var db = Connection(); await db.OpenAsync(ct);
        if (groupId.HasValue && !await OwnsAsync(db, null, groupId.Value, ct)) return NotFound();
        using var cmd = Command(db, null, """
            SELECT S.SongUid, S.Title, S.Artist, S.ChannelThumbnailUrl, S.YouTubeVideoUrl
            FROM dbo.Songs S WHERE EXISTS (
                SELECT 1 FROM dbo.SongGroupMapping M INNER JOIN dbo.SongGroup G ON G.GroupId = M.GroupId
                WHERE M.SongUid = S.SongUid AND G.UserId = @User AND (@Group IS NULL OR G.GroupId = @Group))
            ORDER BY S.SongID DESC OFFSET @Offset ROWS FETCH NEXT 31 ROWS ONLY
            """, ("@User", UserId), ("@Group", (object?)groupId ?? DBNull.Value), ("@Offset", (page - 1) * 30));
        using var rows = await cmd.ExecuteReaderAsync(ct);
        var songs = new List<MobileSong>();
        while (await rows.ReadAsync(ct)) songs.Add(new(rows[0].ToString()!, rows[1].ToString()!, rows[2].ToString()!, rows[3].ToString()!, rows[4].ToString()!));
        var more = songs.Count > 30;
        if (more) songs.RemoveAt(30);
        return Ok(new MobileSongPage(songs, more));
    }

    [HttpPut("groups/{groupId:int}/songs/{songId}")]
    public async Task<IActionResult> Add(int groupId, string songId, CancellationToken ct)
        => await ChangeSong(groupId, songId, true, ct);

    [HttpDelete("groups/{groupId:int}/songs/{songId}")]
    public async Task<IActionResult> Remove(int groupId, string songId, CancellationToken ct)
        => await ChangeSong(groupId, songId, false, ct);

    private async Task<IActionResult> ChangeSong(int groupId, string songId, bool add, CancellationToken ct)
    {
        if (groupId < 1 || !SafeUid.IsMatch(songId)) return BadRequest();
        await using var db = Connection(); await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        if (!await OwnsAsync(db, tx, groupId, ct)) return NotFound();
        if (add)
        {
            using var song = Command(db, tx, "SELECT COUNT(*) FROM dbo.Songs WHERE SongUid = @Song", ("@Song", songId));
            if (Convert.ToInt32(await song.ExecuteScalarAsync(ct)) == 0) return NotFound();
        }
        using var cmd = Command(db, tx, add ? """
            IF NOT EXISTS (SELECT 1 FROM dbo.SongGroupMapping WITH (UPDLOCK, HOLDLOCK) WHERE GroupId = @Group AND SongUid = @Song)
                INSERT INTO dbo.SongGroupMapping (GroupId, SongUid) VALUES (@Group, @Song)
            """ : "DELETE FROM dbo.SongGroupMapping WHERE GroupId = @Group AND SongUid = @Song",
            ("@Group", groupId), ("@Song", songId));
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpDelete("groups/{groupId:int}")]
    public async Task<IActionResult> DeleteGroup(int groupId, CancellationToken ct)
    {
        if (groupId < 1) return BadRequest();
        await using var db = Connection(); await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        if (!await OwnsAsync(db, tx, groupId, ct)) return NotFound();
        using var cmd = Command(db, tx, "DELETE FROM dbo.SongGroupMapping WHERE GroupId = @Id; DELETE FROM dbo.SongGroup WHERE GroupId = @Id AND UserId = @User;",
            ("@Id", groupId), ("@User", UserId));
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return NoContent();
    }

    private async Task<bool> OwnsAsync(SqlConnection db, SqlTransaction? tx, int group, CancellationToken ct)
    {
        using var cmd = Command(db, tx, "SELECT COUNT(*) FROM dbo.SongGroup WITH (UPDLOCK, HOLDLOCK) WHERE GroupId = @Id AND UserId = @User",
            ("@Id", group), ("@User", UserId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}

public sealed record MobileGroup(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("songCount")] int SongCount,
    [property: JsonPropertyName("containsSong")] bool ContainsSong);
public sealed class GroupNameRequest
{
    [Required, StringLength(80, MinimumLength = 1)] public string Name { get; set; } = "";
}
