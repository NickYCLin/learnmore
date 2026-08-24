using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LearnMore.Models
{
    public class LyricSegment
    {
        public int LyricID { get; set; }
        public double TimeStamp { get; set; }
        public string Japanese { get; set; } = string.Empty;
        public string Chinese { get; set; } = string.Empty;
        public string JapaneseRuby { get; set; } = string.Empty;
        public string Roman { get; set; } = string.Empty;
    }

    public class EditLyricsViewModel
    {
        public string SongUid { get; set; } = string.Empty;
        public string MNmae { get; set; } = string.Empty;
        public string YoutubeVideoUrl { get; set; } = string.Empty;
        public string? HighAccuracyStatus { get; set; }
        public string? HighAccuracyStatusReason { get; set; }
        public List<LyricSegment> Lyrics { get; set; } = new();
    }
}
