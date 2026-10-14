namespace LearnMore.Services;

public static class AudioStemPublicationGuard
{
    public static async Task<bool> PublishAsync(Func<Task<bool>> songExists, Action publishFiles, CancellationToken cancellationToken = default)
    {
        if (!await songExists()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        publishFiles();
        return true;
    }
}
