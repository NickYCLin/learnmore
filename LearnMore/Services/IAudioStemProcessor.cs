using LearnMore.Models;

namespace LearnMore.Services;

public interface IAudioStemProcessor
{
    Task ProcessAsync(AudioStemJob job, CancellationToken cancellationToken = default);
}
