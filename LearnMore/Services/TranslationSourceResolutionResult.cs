using LearnMore.Models;

namespace LearnMore.Services;

public enum TranslationSourceKind
{
    PreAligned,
    Marumaru,
    Baha,
    Gpt,
    Fallback
}

public sealed record TranslationSourceResolutionResult(
    List<LyricSegment> Segments,
    TranslationSourceKind Source);
