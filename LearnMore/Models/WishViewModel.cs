namespace LearnMore.Models
{
    public class WishViewModel
    {
        public int Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? UserId { get; set; } = null;
        public string? CreatedAt { get; set; } = null;
        public int IsOk { get; set; } = 0; // 預設 IsOk 為 0
        public string? NickName { get; set; } = null;
        public string? Email { get; set; } = null;
        public string? Avatar { get; set; } = null;
    }
    public class WishReplyViewModel
    {
        public int Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string NickName { get; set; } = string.Empty;
        public string? Email { get; set; } = null;
        public string Avatar { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class WishReplyInputModel
    {
        public string Message { get; set; } = string.Empty;
    }

}
