namespace LearnMore.Models
{
    public class Lyrics
    {
        public int LyricID { get; set; }
        public float TimeStamp { get; set; }
        public string Japanese { get; set; } = string.Empty;
        public string Roman { get; set; } = string.Empty;
        public string Chinese { get; set; } = string.Empty;
    }
}
