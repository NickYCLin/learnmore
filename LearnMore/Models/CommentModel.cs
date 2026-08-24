namespace LearnMore.Models
{
    public class CommentModel
    {
        public Guid CommentId { get; set; } // 留言的唯一 ID
        public string SongUid { get; set; } = string.Empty; // 歌曲的唯一 ID（對應 SongViewModel）
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty; // 使用者名稱（未登入則為 "訪客"）
        public string Message { get; set; } = string.Empty; // 留言內容
        public DateTime TimeStamp { get; set; } // 留言時間
        public bool IsPrivate { get; set; }
        public List<CommentReplyModel> Replies { get; set; } = new List<CommentReplyModel>();
        public string Avatar { get; set; } = string.Empty; // 使用者頭像
    }
    public class CommentReplyModel
    {
        public Guid ReplyId { get; set; } // 回覆的唯一 ID
        public Guid CommentId { get; set; } // 關聯的留言 ID
        public string AdminEmail { get; set; } = string.Empty; // 管理員 Email
        public string AdminName { get; set; } = string.Empty; // 管理員名稱
        public string ReplyMessage { get; set; } = string.Empty; // 回覆內容
        public DateTime ReplyTime { get; set; } // 回覆時間
    }
}
