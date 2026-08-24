namespace LearnMore.Services;

public interface IWhisperHighAccuracyInitialPassService
{
    Task RunHighAccuracyInitialPassAsync(string songUid, CancellationToken cancellationToken = default);
    void EnqueueHighAccuracyInitialPass(string songUid);
}
