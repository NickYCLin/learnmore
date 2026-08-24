namespace LearnMore.Models;

public sealed class PerformerCollectionViewModel
{
    public string Performer { get; set; } = string.Empty;
    public int SongCount { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? SampleTitle { get; set; }
}
