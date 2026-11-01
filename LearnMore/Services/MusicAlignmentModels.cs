using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnMore.Models;

namespace LearnMore.Services;

public sealed record MusicSongSnapshot(string SongUid, string YoutubeUrl, string? Status,
    string? Reason, List<LyricSegment> Lyrics);
public sealed record MusicAlignmentLine(int LyricId, string Japanese, double Before, double? After,
    double Start, double End, double Score, string Source, bool Matched);
public sealed record MusicAlignmentReport(string Model, double? DurationSeconds, int MatchedCount,
    int TotalCount, bool Accepted, string PreviewHash, List<MusicAlignmentLine> Lines,
    RemoteHighAccuracyAlignmentClient.ReferenceAudioEvidence? ReferenceEvidence = null);
public sealed record MusicAlignmentJob(Guid JobId, string SongUid, string State, DateTime CreatedAt,
    DateTime UpdatedAt, string? FailureCode, MusicAlignmentReport? Report, DateTime? AppliedAt);
public sealed class MusicAutomationException(string code, int status = 409) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static class MusicAlignmentValidation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new MusicAutomationException("invalid_stored_data");
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));

    public static MusicAlignmentReport Build(MusicSongSnapshot snapshot,
        RemoteHighAccuracyAlignmentClient.RemoteAlignmentReport remote)
    {
        var response = remote.Response;
        var attempt = remote.Attempt;
        var raw = response?.AlignedLines ?? new();
        var contractValid = response != null && response.SongUid == snapshot.SongUid
            && response.YoutubeUrl == snapshot.YoutubeUrl && response.Language == "ja"
            && response.TotalCount == snapshot.Lyrics.Count && raw.Count == snapshot.Lyrics.Count
            && raw.Select(x => x.LyricId).Distinct().Count() == raw.Count
            && response.DurationSeconds is > 0 && double.IsFinite(response.DurationSeconds.Value)
            && raw.All(x => double.IsFinite(x.Start) && double.IsFinite(x.End) && double.IsFinite(x.Score)
                && x.Start >= 0 && x.End > x.Start && x.End <= response.DurationSeconds
                && x.Score >= 0 && x.Score <= 1)
            && snapshot.Lyrics.All(x => raw.Any(y => y.LyricId == x.LyricID && y.Japanese == x.Japanese));
        var projected = new List<LyricSegment>();
        var accepted = contractValid && attempt.IsSuccess
            && WhisperHighAccuracyInitialPassService.TryBuildAlignedCompleteSnapshot(
                snapshot.Lyrics, attempt.Alignments, out projected, out _)
            && projected.Select((x, i) => x.TimeStamp >= 0 && double.IsFinite(x.TimeStamp)
                && x.TimeStamp <= response!.DurationSeconds
                && (i == 0 || x.TimeStamp > projected[i - 1].TimeStamp)).All(x => x);
        var lines = snapshot.Lyrics.Select((lyric, i) =>
        {
            var evidence = raw.FirstOrDefault(x => x.LyricId == lyric.LyricID);
            var alignment = attempt.Alignments.ElementAtOrDefault(i);
            return new MusicAlignmentLine(lyric.LyricID, lyric.Japanese, lyric.TimeStamp,
                accepted ? projected[i].TimeStamp : null,
                evidence?.Start ?? lyric.TimeStamp, evidence?.End ?? lyric.TimeStamp,
                evidence?.Score ?? 0, evidence?.Source ?? "missing_evidence",
                contractValid && alignment?.IsMatched == true);
        }).ToList();
        var hash = Hash(new { snapshot, model = response?.Model, lines, accepted, response?.ReferenceEvidence });
        return new(response?.Model ?? "unavailable", response?.DurationSeconds,
            lines.Count(x => x.Matched), snapshot.Lyrics.Count, accepted, hash, lines, response?.ReferenceEvidence);
    }
}
