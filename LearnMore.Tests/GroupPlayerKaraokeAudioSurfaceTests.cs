using Xunit;

namespace LearnMore.Tests;

public class GroupPlayerKaraokeAudioSurfaceTests
{
    [Fact]
    public void SongDetail_ShouldExposeAudioStemUrls()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "GroupPlayerController.cs"));

        Assert.Contains("SongAudioStems", source);
        Assert.Contains("InstrumentalAudioUrl", source);
        Assert.Contains("VocalsAudioUrl", source);
        Assert.Contains("GetAudioStemUrl(songUid, \"instrumental\")", source);
        Assert.Contains("GetAudioStemUrl(songUid, \"vocals\")", source);
    }

    [Fact]
    public void PlayView_ShouldRenderKaraokeAudioModesWhenStemsExist()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "GroupPlayer",
            "Play.cshtml"));

        Assert.Contains("group-karaoke-audio-panel", source);
        Assert.Contains("group-karaoke-source-normal", source);
        Assert.Contains("group-karaoke-source-instrumental", source);
        Assert.Contains("group-karaoke-source-vocals", source);
        Assert.Contains("switchGroupKaraokeAudioSource", source);
        Assert.Contains("syncGroupKaraokeAudioFromOriginalSeek", source);
        Assert.Contains("groupKaraokeOriginalControlUntil", source);
        Assert.Contains("groupKaraokeMobileVocalsLyricsDelayStorageKey", source);
        Assert.Contains("loadGroupKaraokeMobileVocalsLyricsDelay", source);
        Assert.Contains("adjustGroupKaraokeMobileVocalsLyricsDelay", source);
        Assert.Contains("getGroupKaraokeLyricsDisplayTime", source);
        Assert.DoesNotContain("groupKaraokeMobileVocalsLyricsDelaySeconds = 0.9", source);
        Assert.DoesNotContain("尚未產生伴奏", source);
    }
}
