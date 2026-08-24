namespace LearnMore.Models
{
    public class TranscribeRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        // Legacy request field. New writes persist this as Performer when Performer is empty.
        public string Cover { get; set; } = string.Empty;
        public string Performer { get; set; } = string.Empty;
        public string ChineseTitleAlias { get; set; } = string.Empty;
        public string YouTubeUrl { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty; // 例如 "zh" 或其他語言代碼
    }
}
