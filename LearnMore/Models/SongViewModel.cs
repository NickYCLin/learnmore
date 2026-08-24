namespace LearnMore.Models
{
    public class SongViewModel
    {
        public int LyricID { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Performer { get; set; } = string.Empty;
        public string Translator { get; set; } = string.Empty;
        public string TranslationSource { get; set; } = string.Empty;
        public string VideoPath { get; set; } = string.Empty;
        public string InstrumentalAudioUrl { get; set; } = string.Empty;
        public string VocalsAudioUrl { get; set; } = string.Empty;
        public List<Lyrics> Lyrics { get; set; } = new();
        public string SongUid { get; set; } = string.Empty;
        public List<CommentModel> Comments { get; set; } = new List<CommentModel>();
        public List<CommentReplyModel> CommentReplys { get; set; } = new List<CommentReplyModel>();
        public string UserEmail { get; set; } = string.Empty;
        public bool IsManage { get; set; }
        public bool IsEnableRoman { get; set; }
        public bool IsEnableAuto { get; set; }
        public bool CanEditTimestamp { get; set; } // 可以編輯時間戳
        public int? AddedByUserId { get; set; } // 上傳者 UserId
        public string? HighAccuracyStatus { get; set; }
        public string? HighAccuracyStatusReason { get; set; }
        public bool HasPendingLyricsProcessing { get; set; }
        public string LyricsProcessingTitle { get; set; } = "這首歌還在整理中";
        public string LyricsProcessingMessage { get; set; } = string.Empty;
    }

    public class SummonRequest
    {
        public string YouTubeLink { get; set; } = string.Empty;
        public string SongTitle { get; set; } = string.Empty;
        public string SongArtist { get; set; } = string.Empty;
        // Legacy request field. New writes persist this as SongPerformer when SongPerformer is empty.
        public string SongCover { get; set; } = string.Empty;
        public string SongPerformer { get; set; } = string.Empty;
        public string ChineseTitleAlias { get; set; } = string.Empty;
        public string SongTranslator { get; set; } = string.Empty;
        public List<LyricEntry> Lyrics { get; set; } = new();
    }

    public class LyricEntry
    {
        public double Time { get; set; }
        public string Japanese { get; set; } = string.Empty;
        public string Chinese { get; set; } = string.Empty;
        public string JapaneseRuby { get; set; } = string.Empty;
        public string Roman { get; set; } = string.Empty;
    }
}
