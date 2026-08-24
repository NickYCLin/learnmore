namespace LearnMore.Models
{
    public class EditSongViewModel
    {
        public Songs Song { get; set; } = new();
        public List<string> Collaborators { get; set; } = new();
    }
}
