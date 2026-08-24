using Xunit;

namespace LearnMore.Tests;

public class LyricsKaraokeSeekSurfaceTests
{
    [Fact]
    public void LyricsView_ShouldSyncKaraokeAudioWhenOriginalProgressIsDragged()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Lyrics",
            "Index.cshtml"));

        Assert.Contains("syncKaraokeAudioFromOriginalSeek", source);
        Assert.Contains("karaokeOriginalSeekControlUntil", source);
        Assert.Contains("karaokeLastOriginalTime", source);
        Assert.Contains("karaokeLastAudioTime", source);
        Assert.Contains("karaokeMobileSyncProfile", source);
        Assert.Contains("syncKaraokeAudioToMobileOriginalClock", source);
        Assert.DoesNotContain("karaokeMobileSyncProfile && isOriginalPlaying()", source);
        Assert.Contains("getKaraokeLyricsDisplayTime", source);
        Assert.Contains("return playbackTime;", source);
        Assert.Contains("targetTimeOverride = null", source);
        Assert.Contains("syncOriginalPlaybackToKaraoke(shouldPlay, true, targetTime)", source);
        Assert.Contains("now < karaokeOriginalSeekControlUntil", source);
        Assert.Contains("Math.max(karaokeSyncSettings.controlMs, 1600)", source);
        Assert.Contains("tooltipInstance.update()", source);
        Assert.DoesNotContain("tooltipInstance.dispose()", source);
        Assert.Contains("seekThreshold: 0.18", source);
        Assert.Contains("!syncKaraokeAudioToMobileOriginalClock() && !syncKaraokeAudioFromOriginalSeek()", source);
    }
}
