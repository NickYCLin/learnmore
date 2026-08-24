using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class LyricsPracticeModeSyncTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void LyricsPracticeModes_ShouldScrollActiveModeWithPlaybackTime()
    {
        var source = Source("LearnMore", "Views", "Lyrics", "Index.cshtml");

        Assert.Contains("function getCurrentLyricIndex(currentTime)", source);
        Assert.Contains("function scrollCurrentLyricIntoActiveMode(currentIndex)", source);
        Assert.Contains("4: '#mode-4 .mode4-line'", source);
        Assert.Contains("5: '#mode-5 .mode5-line'", source);
        Assert.Contains("highlightedLine?.closest('.lyric-card')", source);
        Assert.Contains("const scrollKey = `${currentMode}:${timestamp}`;", source);
        Assert.Contains("scrollCurrentLyricIntoActiveMode(currentIndex);", source);
        Assert.Contains("updateLyricsDisplay(getKaraokeLyricsDisplayTime(getCurrentTime()));", source);
    }
}
