using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperSummonPreparationService : IWhisperSummonPreparationService
{
    private readonly IKuroshiroConversionService _kuroshiroConversionService;

    public WhisperSummonPreparationService(IKuroshiroConversionService kuroshiroConversionService)
    {
        _kuroshiroConversionService = kuroshiroConversionService;
    }

    public async Task<List<LyricEntry>> PrepareLyricsAsync(IReadOnlyCollection<LyricEntry> lyrics, CancellationToken cancellationToken = default)
    {
        var preparedLyrics = new List<LyricEntry>(lyrics.Count);
        foreach (var lyric in lyrics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string rubyHtml = JapaneseRubySanitizer.NormalizeRubyHtml(lyric.JapaneseRuby);
            if (string.IsNullOrWhiteSpace(rubyHtml) && !string.IsNullOrWhiteSpace(lyric.Japanese))
            {
                rubyHtml = await _kuroshiroConversionService.ConvertSingleLineAsync(
                    lyric.Japanese,
                    mode: "furigana",
                    to: "hiragana");
            }

            preparedLyrics.Add(new LyricEntry
            {
                Time = lyric.Time,
                Japanese = lyric.Japanese,
                Chinese = lyric.Chinese,
                JapaneseRuby = rubyHtml,
                Roman = lyric.Roman
            });
        }

        return preparedLyrics;
    }
}
