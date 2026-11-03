using Xunit;

namespace LearnMore.Tests;

public class MediaManageAudioStemStatusSurfaceTests
{
    [Fact]
    public void ManageHeaders_ShouldOverrideCellWrappingWithoutClipping()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore", "wwwroot", "css", "front-pages.css"));

        Assert.Contains(".row.lm-page-shell .lm-manage-table th:nth-child(n) { white-space: nowrap; }", source);
    }

    [Fact]
    public void EditActions_ShouldKeepCompactWidthsOnMobile()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore", "wwwroot", "css", "front-pages.css"));

        Assert.Contains(".lm-edit-song .lm-edit-actions .btn { min-width: 5.5rem; width: auto; flex: 0 0 auto; }", source);
    }

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

        Assert.Contains("<summary>音軌詳情</summary>", source);
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
