using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperLyricsQueryService
{
    Task<EditLyricsViewModel?> GetEditLyricsViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default);
    Task<SongLyricsProcessingSnapshot?> GetSongProcessingSnapshotAsync(string songUid, CancellationToken cancellationToken = default);
    Task<List<Songs>> GetRetryableHighAccuracySongsAsync(CancellationToken cancellationToken = default, bool includeNeedsReview = true)
        => Task.FromResult(new List<Songs>());
}
