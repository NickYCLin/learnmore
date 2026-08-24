namespace LearnMore.Models
{
    public class SongGroup
    {
        public int GroupId { get; set; }
        public string GroupUid { get; set; } = ""; // 🆕 新增
        public string UserId { get; set; } = "";
        public string GroupName { get; set; } = "";
        public DateTime? CreateTime { get; set; }
    }
}
