namespace LearnMore.Services;

public interface IYouTubeMetadataResolverService
{
    Task<YouTubeMetadataResolutionResult> ResolveAsync(string youTubeUrl, string? title, string? artist, CancellationToken cancellationToken = default);
    Task<double?> ResolveDurationSecondsAsync(string youTubeUrl, CancellationToken cancellationToken = default);
}
