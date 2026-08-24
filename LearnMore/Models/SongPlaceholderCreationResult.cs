namespace LearnMore.Models;

public class SongPlaceholderCreationResult
{
    public string SongUid { get; init; } = string.Empty;
    public List<int> LyricIds { get; init; } = new();
}
