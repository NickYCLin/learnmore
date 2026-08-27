using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class SongGroupAuthorizationTests
{
    [Fact]
    public void GroupSongs_ShouldRequireLoginAndOwnershipBeforeQuerying()
    {
        var source = Source("LearnMore", "Controllers", "API", "SongGroupController.cs");
        var action = Slice(
            source,
            "public IActionResult GetSongsInGroup([FromQuery] int groupId)",
            "// 刪除群組中的歌曲");

        Assert.Contains("Session.GetString(\"UserId\")", action);
        Assert.Contains("return Unauthorized();", action);
        Assert.Contains("_repository.IsGroupOwnedByUser(groupId, userId)", action);
        Assert.Contains("return Forbid();", action);
        Assert.True(
            action.IndexOf("IsGroupOwnedByUser", StringComparison.Ordinal)
            < action.IndexOf("GetSongsInGroup(groupId)", StringComparison.Ordinal),
            "Ownership must be checked before loading a private group's songs.");
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
