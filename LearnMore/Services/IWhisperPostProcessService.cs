namespace LearnMore.Services;

public interface IWhisperPostProcessService
{
    Task RunRubyRomanEnrichmentAsync(string songUid, CancellationToken cancellationToken = default);
    void EnqueueRubyRomanEnrichment(string songUid);
}
