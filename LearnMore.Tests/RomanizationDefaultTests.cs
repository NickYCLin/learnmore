using Xunit;

namespace LearnMore.Tests;

public sealed class RomanizationDefaultTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Source(params string[] pathParts)
        => File.ReadAllText(Path.Combine(RepoRoot, Path.Combine(pathParts)));

    [Fact]
    public void LyricsAndGroupPlayer_ShouldEnableRomanizationByDefault()
    {
        var lyricsController = Source("LearnMore", "Controllers", "LyricsController.cs");
        var groupPlayerController = Source("LearnMore", "Controllers", "GroupPlayerController.cs");
        var userController = Source("LearnMore", "Controllers", "UserController.cs");

        Assert.Contains("bool isEnableRoman = true", lyricsController);
        Assert.Contains("bool isEnableRoman = true", groupPlayerController);
        Assert.Contains("int enableRoman = 1", userController);
    }

    [Fact]
    public void LoginController_ShouldEnableRomanizationForNewUsers()
    {
        var loginController = Source("LearnMore", "Controllers", "LoginController.cs");

        Assert.Contains("[EnableRoman]", loginController);
        Assert.Contains(",1)", loginController);
    }
}
