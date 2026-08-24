namespace LearnMore.Services;

public interface IKuroshiroConversionService
{
    Task<string> ConvertSingleLineAsync(string text, string? mode = null, string? to = null);
}
