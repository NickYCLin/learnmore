using LearnMore.Models;

namespace LearnMore.Services;

public interface IYouTubeSubtitleDownloadService
{
    Task<List<LyricSegment>?> TryDownloadSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default);
    Task<List<LyricSegment>?> TryDownloadTranslationSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default);
    Task<List<LyricSegment>?> TryDownloadAutoCaptionTimeAnchorsAsync(string youTubeUrl, CancellationToken cancellationToken = default);
}
