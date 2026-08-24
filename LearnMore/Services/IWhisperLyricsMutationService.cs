using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperLyricsMutationService
{
    Task UpdateLyricsAsync(string songUid, IReadOnlyCollection<LyricSegment> lyrics, CancellationToken cancellationToken = default);
    Task UpdateOrderAsync(string songUid, IReadOnlyList<int> newOrder, CancellationToken cancellationToken = default);
    Task<bool> DeleteLyricAsync(string songUid, int lyricId, CancellationToken cancellationToken = default);
}
