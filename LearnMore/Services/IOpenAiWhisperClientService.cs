namespace LearnMore.Services;

public interface IOpenAiWhisperClientService
{
    Task<string> TranscribeAudioAsync(string audioFilePath, string language);
    Task<string?> BatchTranslateToChineseAsync(string combinedJapanese);
    Task<string> TranslateToChineseAsync(string japaneseText);
    Task<string?> TranslateSongTitleToTraditionalChineseAsync(string songTitle, string? artist = null);
    Task<(string RubyText, string ChineseText)> ProcessJapaneseTextAsync(string text);
}
