namespace LearnMore.Options;

public sealed class WhisperRuntimeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string MyApiKey { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string WhisperJsonPath { get; set; } = string.Empty;
    public string YtDlpCookiesPath { get; set; } = string.Empty;
    public int YtDlpDownloadTimeoutSeconds { get; set; } = 600;
    public bool EnableRuntimeOpenAiTranslation { get; set; }
}
