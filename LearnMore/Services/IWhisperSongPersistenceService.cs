using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperSongPersistenceService
{
    Task<string> AddSongToDatabaseAsync(TranscribeRequest request);
    Task CreateDynamicSongTableAsync(string songUid);
    Task InsertTranscriptionToDynamicTableAsync(string songUid, string transcriptionJson);
    Task InsertManualSegmentsAsync(string songUid, IReadOnlyCollection<TranscriptionSegment> segments, CancellationToken cancellationToken = default);
    Task<string> CreateSummonedSongAsync(SummonRequest request, IReadOnlyCollection<LyricEntry> lyrics, CancellationToken cancellationToken = default);
    Task<SongPlaceholderCreationResult> CreateSongWithPlaceholdersAsync(TranscribeRequest request, IReadOnlyCollection<LyricSegment> segments, CancellationToken cancellationToken = default);
    Task<List<int>> UpdateSongTranslationsAsync(string songUid, IReadOnlyList<LyricSegment> finalSegments, IReadOnlyList<int> existingLyricIds, CancellationToken cancellationToken = default);
    Task UpdateHighAccuracyStatusAsync(string songUid, string? highAccuracyStatus, string? highAccuracyStatusReason = null, CancellationToken cancellationToken = default);
    Task AppendProducerSongAsync(string userEmail, string songUid, CancellationToken cancellationToken = default);
}
