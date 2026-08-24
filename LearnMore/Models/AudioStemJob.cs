namespace LearnMore.Models;

public sealed class AudioStemJob
{
    public int Id { get; init; }
    public string SongUid { get; init; } = string.Empty;
    public string YouTubeVideoUrl { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public int MaxAttempts { get; init; }
}
