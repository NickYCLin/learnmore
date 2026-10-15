using Xunit;

namespace LearnMore.Tests;

public class KaraokeModeSmokeScriptSurfaceTests
{
    [Fact]
    public void KaraokeModeSmokeScript_ShouldCoverModeSwitchingAndProgressSeek()
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "scripts",
            "learnmore_karaoke_mode_smoke.cjs");

        Assert.True(File.Exists(scriptPath), $"Smoke script not found: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("switchKaraokeAudioSource(\"instrumental\")", source);
        Assert.Contains("youtubePlayer.seekTo(seconds, true)", source);
        Assert.Contains("switchKaraokeAudioSource(\"vocals\")", source);
        Assert.Contains("switchKaraokeAudioSource(\"normal\")", source);
        Assert.Contains("Instrumental audio should follow requested progress", source);
        Assert.Contains("Vocal and YouTube time should stay synced", source);
    }

    [Fact]
    public void KaraokeModeSmokeWrapper_ShouldUseBundledNodeRuntime()
    {
        var wrapperPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "scripts",
            "run_learnmore_karaoke_mode_smoke.sh");

        Assert.True(File.Exists(wrapperPath), $"Smoke wrapper not found: {wrapperPath}");

        var source = File.ReadAllText(wrapperPath);
        Assert.Contains("NODE_PATH", source);
        Assert.Contains("learnmore_karaoke_mode_smoke.cjs", source);
    }
}
