namespace LearnMore.Models
{
    public sealed class FavoriteSongViewModel
    {
        public Songs Song { get; set; } = new();
        public List<SongGroup> Groups { get; set; } = new();
    }
}
