using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class MediaDeleteTransactionTests
{
    [Fact]
    public void DeleteSong_ShouldDropLyricsAndDeleteSongInOneTransaction()
    {
        var source = Source("LearnMore", "Controllers", "API", "MediaApiController.cs");
        var action = Slice(
            source,
            "public async Task<IActionResult> DeleteSong",
            "private static async Task<bool> CanDeleteSongAsync");

        Assert.Contains("using var transaction = conn.BeginTransaction();", action);
        Assert.Contains("new SqlCommand(checkQuery, conn, transaction)", action);
        Assert.Contains("CanDeleteSongAsync(conn, transaction", action);
        Assert.Contains("new SqlCommand(dropTableQuery, conn, transaction)", action);
        Assert.Contains("new SqlCommand(deleteSongQuery, conn, transaction)", action);
        Assert.Contains("await transaction.CommitAsync();", action);
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
