using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperSummonPreparationService
{
    Task<List<LyricEntry>> PrepareLyricsAsync(IReadOnlyCollection<LyricEntry> lyrics, CancellationToken cancellationToken = default);
}
