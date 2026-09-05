using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LearnMore.Services.Mobile;

public sealed partial class MobileAccountStore
{
    private static readonly Regex SafeSongUid = new("\\A[A-Za-z0-9_-]{1,100}\\z", RegexOptions.CultureInvariant);

    public static string[] RemoveSongLinks(string links, ISet<string> deleted) => links
        .Split(',').Where(uid => !deleted.Contains(uid.Trim())).ToArray();

    private static async Task DeleteOwnedSongsAsync(SqlConnection db, SqlTransaction tx, int userId, CancellationToken ct)
    {
        // Producer is the legacy ownership field. An explicit different AddedByUserId takes precedence.
        using var query = Command(db, tx, """
            SELECT S.SongUid FROM dbo.Songs S WITH (UPDLOCK, HOLDLOCK)
            WHERE S.AddedByUserId = @Id OR (S.AddedByUserId IS NULL AND EXISTS (
                SELECT 1 FROM dbo.Users U WHERE U.Id = @Id
                AND CHARINDEX(',' + S.SongUid + ',', ',' + ISNULL(U.Producer, '') + ',') > 0))
            """, ("@Id", userId));
        var songs = new HashSet<string>(StringComparer.Ordinal);
        using (var rows = await query.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct)) songs.Add(rows.GetString(0));
        foreach (var uid in songs)
        {
            if (!SafeSongUid.IsMatch(uid)) throw new MobileAuthException("上傳內容清理暫時失敗，帳號尚未刪除，請稍後重試。", 503);
            using var delete = Command(db, tx, """
                DELETE R FROM dbo.CommentReplies R INNER JOIN dbo.Comments C ON C.CommentId = R.CommentId WHERE C.SongUid = @Song;
                DELETE FROM dbo.Comments WHERE SongUid = @Song;
                DELETE FROM dbo.ErrorReports WHERE ErrorSongUid = @Song;
                DELETE FROM dbo.SongGroupMapping WHERE SongUid = @Song;
                IF OBJECT_ID('dbo.SongAliases', 'U') IS NOT NULL DELETE FROM dbo.SongAliases WHERE SongUid = @Song;
                IF OBJECT_ID('dbo.SongsDataHistory', 'U') IS NOT NULL DELETE FROM dbo.SongsDataHistory WHERE SongUid = @Song;
                IF OBJECT_ID('dbo.SongsData', 'U') IS NOT NULL DELETE FROM dbo.SongsData WHERE SongUid = @Song;
                IF OBJECT_ID('dbo.SongAudioStemJobs', 'U') IS NOT NULL DELETE FROM dbo.SongAudioStemJobs WHERE SongUid = @Song;
                IF OBJECT_ID('dbo.SongAudioStems', 'U') IS NOT NULL DELETE FROM dbo.SongAudioStems WHERE SongUid = @Song;
                DELETE FROM dbo.Songs WHERE SongUid = @Song;
                INSERT INTO dbo.MobileFileDeletionJobs (FileName, Kind) VALUES (@Song, 'song');
                """, ("@Song", uid));
            await delete.ExecuteNonQueryAsync(ct);
            // Only validated server-read UIDs can enter the legacy table identifier.
            using var lyrics = Command(db, tx, $"DROP TABLE IF EXISTS dbo.[Songs_{uid}]");
            await lyrics.ExecuteNonQueryAsync(ct);
        }
        if (songs.Count == 0) return;
        using var links = Command(db, tx, "SELECT Id, ISNULL(Producer,''), ISNULL(Collaboration,'') FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)");
        var updates = new List<(int Id, string Producer, string Collaboration)>();
        using (var rows = await links.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                var producer = string.Join(',', RemoveSongLinks(rows.GetString(1), songs));
                var collaboration = string.Join(',', RemoveSongLinks(rows.GetString(2), songs));
                if (producer != rows.GetString(1) || collaboration != rows.GetString(2)) updates.Add((rows.GetInt32(0), producer, collaboration));
            }
        }
        foreach (var update in updates)
        {
            using var cmd = Command(db, tx, "UPDATE dbo.Users SET Producer = @Producer, Collaboration = @Collaboration WHERE Id = @Id",
                ("@Producer", update.Producer), ("@Collaboration", update.Collaboration), ("@Id", update.Id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
