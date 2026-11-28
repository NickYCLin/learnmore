using LearnMore.Models;

namespace LearnMore.Services;

public static class SongTranslationCompleteness
{
    public static bool IsUsableChinese(string? text) =>
        text?.Trim() is { Length: > 0 } value &&
        value is not ("翻譯中..." or "翻譯中…" or "待翻譯" or "翻譯待補");

    public static bool HasCompleteChineseTranslations(IReadOnlyList<LyricSegment> segments) =>
        segments.Count > 0 && segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment.Japanese) && IsUsableChinese(segment.Chinese));
}
