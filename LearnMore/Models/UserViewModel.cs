using System.ComponentModel.DataAnnotations;

namespace LearnMore.Models
{
    public class UserViewModel
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "暱稱是必填的")]
        public string NickName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Avatar { get; set; }
    }
}
