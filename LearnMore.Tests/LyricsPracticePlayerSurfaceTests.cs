using Xunit;

namespace LearnMore.Tests;

public class LyricsPracticePlayerSurfaceTests
{
    private static string ReadSource(params string[] parts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(parts)));
    }

    [Fact]
    public void LyricsView_ShouldConfigureInlineYouTubePlayerVarsAndRecoveryPrompt()
    {
        var source = ReadSource("LearnMore", "Views", "Lyrics", "Index.cshtml");

        Assert.Contains("playerVars", source);
        Assert.Contains("playsinline: 1", source);
        Assert.Contains("origin: window.location.origin", source);
        Assert.Contains("controls: 1", source);
        Assert.Contains("rel: 0", source);
        Assert.Contains("'onError': onYouTubePlayerError", source);
        Assert.Contains("'onAutoplayBlocked': onYouTubeAutoplayBlocked", source);
        Assert.Contains("點一下繼續", source);
        Assert.Contains("playback-continue-prompt", source);
        Assert.Contains("navigator.wakeLock", source);
        Assert.Contains("requestPracticeWakeLock", source);
        Assert.Contains("releasePracticeWakeLock", source);
        Assert.Contains("document.hidden", source);
        Assert.Contains("learnmore.lyrics.autoReplay", source);
        Assert.Contains("整首重播", source);
        Assert.Contains("單句循環", source);
        Assert.Contains("設 A 點", source);
        Assert.Contains("practice-skip-back", source);
        Assert.Contains("applyPracticePlaybackRate", source);
        Assert.Contains("audio.playbackRate = practicePlaybackRate", source);
        Assert.Contains("handlePlaybackEnded", source);
        Assert.Contains("video-sticky-stack", source);
        Assert.Contains("practice-controls-mobile", source);
        Assert.Contains("practice-mobile-only", source);
        Assert.Contains("isPracticeMobileViewport", source);
        Assert.Contains("syncPracticeControlsToViewport", source);
        Assert.Contains("PRACTICE_MOBILE_MEDIA", source);
        Assert.Contains("(max-width: 991.98px)", source);
    }

    [Fact]
    public void LyricsCss_ShouldStickOfficialPlayerOnMobile()
    {
        var source = ReadSource("LearnMore", "wwwroot", "css", "lyrics.css");

        Assert.Contains(".video-sticky-stack", source);
        Assert.Contains("position: sticky", source);
        Assert.Contains("display: contents", source);
        Assert.Contains("overflow-x: visible", source);
        Assert.Contains(".practice-controls", source);
        Assert.Contains(".practice-control-toggles input[type=\"checkbox\"]", source);
        Assert.Contains(".practice-controls-mobile", source);
        Assert.Contains(".group-practice-controls", source);
        Assert.Contains(".practice-mobile-only", source);
        Assert.Contains("@media (max-width: 991.98px)", source);
    }

    [Fact]
    public void GroupPlayerView_ShouldSetOriginSpeedAndStickyPlayer()
    {
        var source = ReadSource("LearnMore", "Views", "GroupPlayer", "Play.cshtml");

        Assert.Contains("origin: window.location.origin", source);
        Assert.Contains("group-practice-speed", source);
        Assert.Contains("applyGroupPracticePlaybackRate", source);
        Assert.Contains("audio.playbackRate = groupPracticePlaybackRate", source);
        Assert.Contains("video-sticky-stack", source);
        Assert.Contains("requestGroupPracticeWakeLock", source);
        Assert.Contains("releaseGroupPracticeWakeLock", source);
        Assert.Contains("display: contents", source);
        Assert.Contains(".group-player-page-container > .card", source);
        Assert.Contains("overflow: visible", source);
        Assert.Contains("isGroupPracticeMobileViewport", source);
        Assert.Contains("syncGroupPracticeControlsToViewport", source);
        Assert.Contains("(max-width: 991.98px)", source);
    }
}
