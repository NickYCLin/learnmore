using LearnMore.Models;

namespace LearnMore.Services;

public interface IWhisperTranscribeWorkflowService
{
    Task<string> ExecuteAsync(TranscribeRequest request);
}
