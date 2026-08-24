using LearnMore.Controllers;

namespace LearnMore.Services;

public class KuroshiroConversionService : IKuroshiroConversionService
{
    private readonly KuroshiroController _kuroshiroController;

    public KuroshiroConversionService(KuroshiroController kuroshiroController)
    {
        _kuroshiroController = kuroshiroController;
    }

    public Task<string> ConvertSingleLineAsync(string text, string? mode = null, string? to = null)
    {
        return _kuroshiroController.ConvertSingleLineAsync(text, mode, to);
    }
}
