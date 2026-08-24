using Xunit;

namespace LearnMore.Tests;

public class KaraokeModeVisualStateSurfaceTests
{
    [Fact]
    public void LyricsCss_ShouldNotHighlightEntireAudioPanelWhenKaraokeModeIsActive()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "wwwroot",
            "css",
            "lyrics.css"));

        Assert.DoesNotContain(".karaoke-audio-ready.is-active", source);
        Assert.DoesNotContain("karaoke-audio-ready.is-active .karaoke-audio-dot", source);
        Assert.Contains(".karaoke-source-button.active", source);
    }
}
