using System.Reflection.Metadata.Ecma335;

namespace LearnMore.Models
{
    public class Songs
    {
        public int SongID { get; set; }
        public int LyricID { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string? Performer { get; set; }
        public string? Translator { get; set; }
        public string? TranslationSource { get; set; }
        public string YouTubeVideoUrl { get; set; } = string.Empty;
        public string? InstrumentalAudioUrl { get; set; }
        public string? VocalsAudioUrl { get; set; }
        public string ChannelThumbnailUrl { get; set; } = string.Empty;
        public string SongUid { get; set; } = string.Empty;
        public string? SongType { get; set; }
        public string? HighAccuracyStatus { get; set; }
        public string? HighAccuracyStatusReason { get; set; }
        public bool HasInstrumentalAudioStem { get; set; }
        public bool HasVocalsAudioStem { get; set; }
        public int? AddedByUserId { get; set; }
    }

    public class UserSongs
    {
        public string Producer { get; set; } = string.Empty;
        public string Collaboration { get; set; } = string.Empty;
    }

    public class ManageViewModel
    {
        public List<Songs> ProducerSongs { get; set; } = new List<Songs>();
        public List<Songs> CollaborationSongs { get; set; } = new List<Songs>();
    }
}
