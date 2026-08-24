namespace LearnMore.Models;

public class SongLyricsProcessingSnapshot
{
    public string SongUid { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string YouTubeUrl { get; init; } = string.Empty;
    public List<LyricSegment> Lyrics { get; set; } = new();
}
