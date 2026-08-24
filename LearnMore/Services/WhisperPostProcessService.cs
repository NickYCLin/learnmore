using LearnMore.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace LearnMore.Services;

public class WhisperPostProcessService : IWhisperPostProcessService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WhisperPostProcessService> _logger;

    public WhisperPostProcessService(
        IServiceScopeFactory scopeFactory,
        ILogger<WhisperPostProcessService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunRubyRomanEnrichmentAsync(string songUid, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var kuroshiroController = scope.ServiceProvider.GetRequiredService<KuroshiroController>();
        var (rubyInput, romanInput) = BuildEnrichmentRequests(songUid);

        await kuroshiroController.ConvertAndUpdateInternalAsync(rubyInput);

        cancellationToken.ThrowIfCancellationRequested();

        await kuroshiroController.ConvertAndUpdateInternalAsync(romanInput);
    }

    public static (KuroshiroController.ConvertRequest RubyRequest, KuroshiroController.ConvertRequest RomanRequest) BuildEnrichmentRequests(string songUid)
    {
        return (
            new KuroshiroController.ConvertRequest
            {
                SongUid = songUid,
                Column = "JapaneseRuby",
                Mode = "furigana",
                To = "hiragana"
            },
            new KuroshiroController.ConvertRequest
            {
                SongUid = songUid,
                Column = "Roman",
                Mode = "spaced",
                To = "romaji"
            });
    }

    public void EnqueueRubyRomanEnrichment(string songUid)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunRubyRomanEnrichmentAsync(songUid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "背景補 Ruby/Roman 失敗 songUid={SongUid}", songUid);
            }
        });
    }
}
