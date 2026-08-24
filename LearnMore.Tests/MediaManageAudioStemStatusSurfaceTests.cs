using Xunit;

namespace LearnMore.Tests;

public class MediaManageAudioStemStatusSurfaceTests
{
    [Fact]
    public void ManageView_ShouldRenderAudioStemStatusColumn()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Manage.cshtml"));

        Assert.Contains("<th>音軌</th>", source);
        Assert.Contains("GetAudioStemStatusLabel(song)", source);
        Assert.Contains("伴奏/人聲完成", source);
        Assert.Contains("未處理", source);
    }

    [Fact]
    public void ManageQuery_ShouldReadAudioStemStatusFromSongAudioStems()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperManageQueryService.cs"));

        Assert.Contains("SongAudioStems", source);
        Assert.Contains("HasInstrumentalAudioStem", source);
        Assert.Contains("HasVocalsAudioStem", source);
        Assert.Contains("stems.StemKind = N'instrumental'", source);
        Assert.Contains("stems.StemKind = N'vocals'", source);
    }
}
