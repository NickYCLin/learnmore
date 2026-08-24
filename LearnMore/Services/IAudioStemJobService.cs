using LearnMore.Models;

namespace LearnMore.Services;

public interface IAudioStemJobService
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
    Task EnqueueSongAsync(string songUid, string? youTubeVideoUrl, CancellationToken cancellationToken = default);
    Task<AudioStemJob?> TryLeaseNextJobAsync(CancellationToken cancellationToken = default);
    Task MarkJobCompletedAsync(AudioStemJob job, CancellationToken cancellationToken = default);
    Task MarkJobFailedAsync(AudioStemJob job, string error, CancellationToken cancellationToken = default);
    Task RegisterCompletedStemsAsync(string songUid, string instrumentalPath, string vocalsPath, string modelName, string source, CancellationToken cancellationToken = default);
}
