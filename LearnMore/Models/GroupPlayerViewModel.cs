using System.Collections.Generic;

namespace LearnMore.Models
{
    public class GroupPlayerViewModel
    {
        public string GroupUid { get; set; } = ""; // 🆕 改用 GroupUid
        public string GroupName { get; set; } = "";
        public List<SongItem> Songs { get; set; } = new List<SongItem>();
        public string? UserEmail { get; set; } // 🆕 加入使用者 Email 供留言/私密判斷
    }

    public class SongItem
    {
        public string SongUid { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string YouTubeVideoUrl { get; set; } = "";
        public string Performer { get; set; } = "";
    }
}
