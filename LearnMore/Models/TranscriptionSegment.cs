namespace LearnMore.Models
{
    public class TranscriptionSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Chinese { get; set; } = string.Empty;
    }
}
