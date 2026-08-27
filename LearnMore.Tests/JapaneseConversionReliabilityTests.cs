using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class JapaneseConversionReliabilityTests
{
    [Fact]
    public void CrawlerBatchConversion_ShouldFailWithoutPartialUpdatesOnCountMismatch()
    {
        var source = Source("LearnMore", "Controllers", "API", "CrawlerController.cs");
        var action = Slice(
            source,
            "public async Task<IActionResult> ConvertAndUpdateOptimized",
            "private async Task EnsureColumnExists");

        Assert.Contains("if (convertedArray.Length != lyricsList.Count)", action);
        Assert.Contains("WHERE Japanese IS NOT NULL ORDER BY LyricID", action);
        Assert.Contains("未更新任何資料", action);
        Assert.DoesNotContain("Math.Min(convertedArray.Length, lyricsList.Count)", action);
        Assert.DoesNotContain("Take(takeCount)", action);
        Assert.True(
            action.IndexOf("未更新任何資料", StringComparison.Ordinal)
            < action.IndexOf("BeginTransaction", StringComparison.Ordinal),
            "A mismatched conversion must stop before starting database updates.");
    }

    [Fact]
    public void KuroshiroProcess_ShouldDrainBothStreamsAndEnforceTimeout()
    {
        var source = Source("LearnMore", "Controllers", "API", "KuroshiroController.cs");
        var method = Slice(
            source,
            "private async Task<List<string>> ConvertWithNodeAsync",
            "private async Task<List<(int LyricID, string Japanese)>> LoadLyricsAsync");

        Assert.Contains("var standardOutputTask = process.StandardOutput.ReadToEndAsync();", method);
        Assert.Contains("var errorOutputTask = process.StandardError.ReadToEndAsync();", method);
        Assert.Contains("Task.WhenAll(standardOutputTask, errorOutputTask)", method);
        Assert.Contains("WaitForExitAsync(timeout.Token)", method);
        Assert.Contains("process.Kill(entireProcessTree: true)", method);
        Assert.Contains("throw new TimeoutException", method);
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
