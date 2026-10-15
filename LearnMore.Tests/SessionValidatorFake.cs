using LearnMore.Services;

namespace LearnMore.Tests;

public sealed class SessionValidatorFake : IAccountSessionValidator
{
    public bool Available { get; set; } = true;
    public bool Throw { get; set; }
    public bool Exists(int userId, string? email = null)
        => Throw ? throw new InvalidOperationException("database unavailable") : Available;
}
