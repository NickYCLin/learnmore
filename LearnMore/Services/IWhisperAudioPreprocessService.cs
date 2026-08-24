namespace LearnMore.Services;

public interface IWhisperAudioPreprocessService
{
    Task<WhisperAudioPreprocessResult> TrimLeadingSilenceAsync(string audioFilePath, CancellationToken cancellationToken = default);
}
