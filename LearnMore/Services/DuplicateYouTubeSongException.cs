namespace LearnMore.Services;

public sealed class DuplicateYouTubeSongException : Exception
{
    public DuplicateYouTubeSongException(string existingSongUid, string videoId)
        : base($"YouTube 影片已存在：{existingSongUid}")
    {
        ExistingSongUid = existingSongUid;
        VideoId = videoId;
    }

    public string ExistingSongUid { get; }
    public string VideoId { get; }
}
