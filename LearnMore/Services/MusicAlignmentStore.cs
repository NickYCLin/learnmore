using System.Data;
using System.Data.SqlClient;
using System.Text.RegularExpressions;
using LearnMore.Models;
using static LearnMore.Services.MusicAlignmentValidation;

namespace LearnMore.Services;

public sealed class MusicAlignmentStore(IConfiguration config)
{
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var stream = typeof(MusicAlignmentStore).Assembly.GetManifestResourceStream("LearnMore.MusicAutomation.sql")!;
        using var reader = new StreamReader(stream);
        await using var db = await OpenAsync(ct);
        using var command = new SqlCommand(await reader.ReadToEndAsync(ct), db);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqlConnection(config.GetConnectionString("DefaultConnection"));
        try
        {
            await db.OpenAsync(ct);
            // Pooled SQL sessions may retain the previous transaction isolation.
            using var reset = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", db);
            await reset.ExecuteNonQueryAsync(ct);
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async Task LockAsync(SqlConnection db, SqlTransaction tx, string resource, CancellationToken ct)
    {
        using var q = new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000; SELECT @r", db, tx);
        q.Parameters.AddWithValue("@resource", resource);
        if (Convert.ToInt32(await q.ExecuteScalarAsync(ct)) < 0) throw new MusicAutomationException("busy");
    }

    public static string VideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new MusicAutomationException("invalid_youtube_url", 400);
        var id = uri.Host.ToLowerInvariant() switch
        {
            "youtu.be" => uri.AbsolutePath.Trim('/'),
            "youtube.com" or "www.youtube.com" or "m.youtube.com" when uri.AbsolutePath == "/watch"
                => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("v", out var v) ? v.ToString() : "",
            _ => ""
        };
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) throw new MusicAutomationException("invalid_youtube_url", 400);
        return id;
    }

    private static string CanonicalUid(string uid) => Guid.TryParseExact(uid, "D", out var id)
        ? id.ToString("D") : throw new MusicAutomationException("invalid_song_uid", 400);

    private static async Task<MusicSongSnapshot> SnapshotAsync(SqlConnection db, SqlTransaction tx, string uid, CancellationToken ct)
    {
        uid = CanonicalUid(uid);
        using var song = new SqlCommand("SELECT YouTubeVideoUrl,HighAccuracyStatus,HighAccuracyStatusReason FROM dbo.Songs WITH(UPDLOCK,HOLDLOCK) WHERE SongUid=@uid", db, tx);
        song.Parameters.AddWithValue("@uid", uid);
        string url; string? status; string? reason;
        await using (var r = await song.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new MusicAutomationException("song_not_found", 404);
            url = r.IsDBNull(0) ? "" : r.GetString(0);
            status = r.IsDBNull(1) ? null : r.GetString(1);
            reason = r.IsDBNull(2) ? null : r.GetString(2);
            if (await r.ReadAsync(ct)) throw new MusicAutomationException("ambiguous_song");
        }
        VideoId(url);
        using var query = new SqlCommand($"SELECT LyricID,TimeStamp,Japanese,Chinese,JapaneseRuby,Roman FROM dbo.[Songs_{uid}] WITH(UPDLOCK,HOLDLOCK) ORDER BY LyricID", db, tx);
        var lyrics = new List<LyricSegment>();
        await using var rows = await query.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (lyrics.Count >= 500) throw new MusicAutomationException("too_many_lyrics", 400);
            lyrics.Add(new LyricSegment { LyricID = rows.GetInt32(0), TimeStamp = rows.GetDouble(1),
                Japanese = rows.IsDBNull(2) ? "" : rows.GetString(2), Chinese = rows.IsDBNull(3) ? "" : rows.GetString(3),
                JapaneseRuby = rows.IsDBNull(4) ? "" : rows.GetString(4), Roman = rows.IsDBNull(5) ? "" : rows.GetString(5) });
        }
        if (lyrics.Count == 0 || lyrics.Any(x => x.LyricID <= 0 || string.IsNullOrWhiteSpace(x.Japanese)
            || x.Japanese.Length > 2000 || !double.IsFinite(x.TimeStamp)))
            throw new MusicAutomationException("formal_lyrics_required", 400);
        return new(uid, url, status, reason, lyrics);
    }

    public async Task<MusicAlignmentJob> StartAsync(string requestId, string? songUid, string? youtubeUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || (songUid == null && youtubeUrl == null))
            throw new MusicAutomationException("request_id_and_song_required", 400);
        var videoId = youtubeUrl == null ? null : VideoId(youtubeUrl);
        if (songUid != null) songUid = CanonicalUid(songUid);
        await using var db = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(db, tx, "LearnMore.CodexBackfill", ct);
        await LockAsync(db, tx, "LearnMore.MusicAutomation.Start", ct);
        if (songUid == null)
        {
            using var find = new SqlCommand("SELECT SongUid,YouTubeVideoUrl FROM dbo.Songs WHERE YouTubeVideoUrl LIKE @video", db, tx);
            find.Parameters.AddWithValue("@video", "%" + videoId + "%");
            await using var r = await find.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (VideoId(r.GetString(1)) != videoId) continue;
                if (songUid != null) throw new MusicAutomationException("ambiguous_video");
                songUid = CanonicalUid(r.GetString(0));
            }
            if (songUid == null) throw new MusicAutomationException("upload_song_first", 404);
        }
        using var prior = new SqlCommand("SELECT JobId,SnapshotJson FROM dbo.MusicAlignmentJobs WHERE RequestKey=@key UNION SELECT j.JobId,j.SnapshotJson FROM dbo.MusicAlignmentRequests r JOIN dbo.MusicAlignmentJobs j ON j.JobId=r.JobId WHERE r.RequestKey=@key", db, tx);
        prior.Parameters.AddWithValue("@key", Hash(requestId));
        Guid? existing = null;
        await using (var r = await prior.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                var old = Parse<MusicSongSnapshot>(r.GetString(1));
                if (old.SongUid != songUid || (videoId != null && VideoId(old.YoutubeUrl) != videoId))
                    throw new MusicAutomationException("request_id_reused");
                existing = r.GetGuid(0);
            }
        }
        if (existing.HasValue) { await tx.CommitAsync(ct); return await GetAsync(existing.Value, ct); }
        var snapshot = await SnapshotAsync(db, tx, songUid, ct);
        if (videoId != null && VideoId(snapshot.YoutubeUrl) != videoId) throw new MusicAutomationException("song_video_mismatch", 400);
        if (snapshot.Status == "high_accuracy_processing") throw new MusicAutomationException("song_is_processing");
        var fingerprint = Hash(snapshot);
        using var active = new SqlCommand("SELECT TOP(1) JobId FROM dbo.MusicAlignmentJobs WHERE SongUid=@uid AND SnapshotHash=@hash AND State IN ('queued','processing','verified') ORDER BY CreatedAt", db, tx);
        active.Parameters.AddWithValue("@uid", songUid); active.Parameters.AddWithValue("@hash", fingerprint);
        var activeId = await active.ExecuteScalarAsync(ct);
        if (activeId is Guid activeGuid)
        {
            using var alias = new SqlCommand("INSERT dbo.MusicAlignmentRequests(RequestKey,JobId) VALUES(@key,@id)", db, tx);
            alias.Parameters.AddWithValue("@key", Hash(requestId)); alias.Parameters.AddWithValue("@id", activeGuid);
            await alias.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return await GetAsync(activeGuid, ct);
        }
        using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.MusicAlignmentJobs WHERE State IN ('queued','processing')", db, tx);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= 100) throw new MusicAutomationException("queue_full", 429);
        var id = Guid.NewGuid();
        using var insert = new SqlCommand("INSERT dbo.MusicAlignmentJobs(JobId,RequestKey,SongUid,SnapshotHash,SnapshotJson,State) VALUES(@id,@key,@uid,@hash,@snapshot,'queued')", db, tx);
        insert.Parameters.AddWithValue("@id", id); insert.Parameters.AddWithValue("@key", Hash(requestId));
        insert.Parameters.AddWithValue("@uid", songUid); insert.Parameters.AddWithValue("@hash", fingerprint);
        insert.Parameters.AddWithValue("@snapshot", Serialize(snapshot));
        await insert.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<MusicAlignmentJob> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        using var q = new SqlCommand("SELECT SongUid,State,CreatedAt,UpdatedAt,FailureCode,ReportJson,AppliedAt FROM dbo.MusicAlignmentJobs WHERE JobId=@id", db);
        q.Parameters.AddWithValue("@id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new MusicAutomationException("job_not_found", 404);
        return new(id, r.GetString(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
            DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : Parse<MusicAlignmentReport>(r.GetString(5)),
            r.IsDBNull(6) ? null : DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc));
    }

    public async Task<(Guid Id, MusicSongSnapshot Snapshot)?> ClaimAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        using var q = new SqlCommand("""
            UPDATE dbo.MusicAlignmentJobs SET State='interrupted',FailureCode='worker_lease_expired',UpdatedAt=SYSUTCDATETIME()
            WHERE State='processing' AND LeaseUntil<SYSUTCDATETIME();
            ;WITH next_job AS (SELECT TOP(1) * FROM dbo.MusicAlignmentJobs WITH(UPDLOCK,READPAST,READCOMMITTEDLOCK)
                WHERE State='queued' ORDER BY CreatedAt)
            UPDATE next_job SET State='processing',UpdatedAt=SYSUTCDATETIME(),LeaseUntil=DATEADD(minute,45,SYSUTCDATETIME())
            OUTPUT inserted.JobId,inserted.SnapshotJson;
            """, db);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), Parse<MusicSongSnapshot>(r.GetString(1))) : null;
    }

    public async Task CompleteAsync(Guid id, MusicAlignmentReport? report, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        using var q = new SqlCommand("UPDATE dbo.MusicAlignmentJobs SET State=@state,ReportJson=@report,FailureCode=@failure,LeaseUntil=NULL,UpdatedAt=SYSUTCDATETIME() WHERE JobId=@id AND State='processing' AND LeaseUntil>=SYSUTCDATETIME()", db);
        q.Parameters.AddWithValue("@id", id);
        q.Parameters.AddWithValue("@state", report == null ? "failed" : report.Accepted ? "verified" : "needs_review");
        q.Parameters.AddWithValue("@report", report == null ? DBNull.Value : Serialize(report));
        q.Parameters.AddWithValue("@failure", report == null ? "alignment_failed" : report.Accepted ? DBNull.Value : "verification_incomplete");
        if (await q.ExecuteNonQueryAsync(ct) != 1) throw new MusicAutomationException("job_lease_lost");
    }

    public async Task<MusicAlignmentJob> ApplyAsync(Guid id, string previewHash, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(db, tx, "LearnMore.CodexBackfill", ct);
        using var q = new SqlCommand("SELECT State,SnapshotJson,ReportJson FROM dbo.MusicAlignmentJobs WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@id", db, tx);
        q.Parameters.AddWithValue("@id", id);
        string state; MusicSongSnapshot before; MusicAlignmentReport report;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new MusicAutomationException("job_not_found", 404);
            state = r.GetString(0); before = Parse<MusicSongSnapshot>(r.GetString(1));
            if (r.IsDBNull(2)) throw new MusicAutomationException("report_not_ready");
            report = Parse<MusicAlignmentReport>(r.GetString(2));
        }
        if (string.IsNullOrEmpty(previewHash) || previewHash != report.PreviewHash) throw new MusicAutomationException("preview_changed");
        if (state == "applied") { await tx.CommitAsync(ct); return await GetAsync(id, ct); }
        if (state != "verified" || !report.Accepted || report.MatchedCount != before.Lyrics.Count)
            throw new MusicAutomationException("verification_required");
        var current = await SnapshotAsync(db, tx, before.SongUid, ct);
        if (Hash(current) != Hash(before)) throw new MusicAutomationException("song_changed_since_analysis");
        if (before.Lyrics.Any(x => string.IsNullOrWhiteSpace(x.Chinese) || string.IsNullOrWhiteSpace(x.JapaneseRuby)
            || string.IsNullOrWhiteSpace(x.Roman) || x.Chinese is "翻譯中..." or "待翻譯" or "翻譯待補"))
            throw new MusicAutomationException("text_enrichment_required");
        var projected = Parse<MusicSongSnapshot>(Serialize(before));
        for (var i = 0; i < before.Lyrics.Count; i++)
        {
            var line = report.Lines[i];
            if (line.LyricId != projected.Lyrics[i].LyricID || line.After == null || !double.IsFinite(line.After.Value)
                || line.After < 0 || line.After > report.DurationSeconds
                || (i > 0 && line.After <= projected.Lyrics[i - 1].TimeStamp))
                throw new MusicAutomationException("invalid_verified_plan");
            projected.Lyrics[i].TimeStamp = line.After.Value;
        }
        projected = projected with { Status = "high_accuracy_completed", Reason = $"同影片音訊已完成逐句時間校正，共 {before.Lyrics.Count} 行，命中 {before.Lyrics.Count} 行。" };
        using var backup = new SqlCommand("INSERT dbo.MusicAlignmentBackups(JobId,BeforeJson,AfterHash) VALUES(@id,@before,@after)", db, tx);
        backup.Parameters.AddWithValue("@id", id); backup.Parameters.AddWithValue("@before", Serialize(before)); backup.Parameters.AddWithValue("@after", Hash(projected));
        await backup.ExecuteNonQueryAsync(ct);
        foreach (var line in projected.Lyrics)
        {
            using var update = new SqlCommand($"UPDATE dbo.[Songs_{CanonicalUid(before.SongUid)}] SET TimeStamp=@time WHERE LyricID=@lyric", db, tx);
            update.Parameters.AddWithValue("@time", line.TimeStamp); update.Parameters.AddWithValue("@lyric", line.LyricID);
            if (await update.ExecuteNonQueryAsync(ct) != 1) throw new MusicAutomationException("lyric_update_conflict");
        }
        using var status = new SqlCommand("UPDATE dbo.Songs SET HighAccuracyStatus=@status,HighAccuracyStatusReason=@reason WHERE SongUid=@uid", db, tx);
        status.Parameters.AddWithValue("@status", projected.Status!); status.Parameters.AddWithValue("@reason", projected.Reason!);
        status.Parameters.AddWithValue("@uid", before.SongUid);
        if (await status.ExecuteNonQueryAsync(ct) != 1) throw new MusicAutomationException("song_update_conflict");
        if (Hash(await SnapshotAsync(db, tx, before.SongUid, ct)) != Hash(projected)) throw new MusicAutomationException("readback_failed");
        using var finish = new SqlCommand("UPDATE dbo.MusicAlignmentJobs SET State='applied',AppliedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME() WHERE JobId=@id", db, tx);
        finish.Parameters.AddWithValue("@id", id); await finish.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }
}
