namespace LearnMore.Models;

public sealed record PaymentCallbackField(string Name, string Value);

public sealed record PaymentCallbackViewModel(
    string Title,
    IReadOnlyList<PaymentCallbackField> Fields)
{
    public string GetValue(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
        ?? string.Empty;
}
