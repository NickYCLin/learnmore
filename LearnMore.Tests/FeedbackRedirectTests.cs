using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class FeedbackRedirectTests
{
    [Fact]
    public void FeedbackPost_ShouldRedirectAnonymousUsersToExistingLoginAction()
    {
        var source = Source("LearnMore", "Controllers", "UserController.cs");
        var action = Slice(
            source,
            "public IActionResult Feedback(string Title, string Content)",
            "#endregion");

        Assert.Contains("RedirectToAction(\"Index\", \"Login\")", action);
        Assert.DoesNotContain("RedirectToAction(\"Login\", \"Account\")", action);
    }

    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after start: {endMarker}");

        return source[start..end];
    }
}
