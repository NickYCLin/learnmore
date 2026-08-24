using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperTranslationSourceService
{
    Task<List<LyricSegment>?> TryPreAlignAsync(string title, string artist, IReadOnlyList<LyricSegment> timestampSegments, CancellationToken cancellationToken = default, bool preferMarumaruLineCount = false);
    Task<TranslationSourceResolutionResult> ResolveFinalSegmentsAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, List<LyricSegment>? preAlignedSegments, CancellationToken cancellationToken = default);
}
