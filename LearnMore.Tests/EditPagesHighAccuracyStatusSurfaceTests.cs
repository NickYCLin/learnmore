using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class EditPagesHighAccuracyStatusSurfaceTests
{
    [Fact]
    public void SharedPartial_ShouldExist_ForHighAccuracyStatusSummary()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_HighAccuracyStatusSummary.cshtml"));

        Assert.Contains("@model LearnMore.Models.HighAccuracyStatusSummaryViewModel", source);
        Assert.Contains("高精度處理中", source);
        Assert.Contains("@Model.ReasonText", source);
    }

    [Fact]
    public void EditView_ShouldUseSharedHighAccuracyStatusPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Edit.cshtml"));

        Assert.Contains("_HighAccuracyStatusSummary", source);
        Assert.DoesNotContain("GetHighAccuracyStatusLabel", source);
    }

    [Fact]
    public void EditLyricsView_ShouldUseSharedHighAccuracyStatusPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "EditLyrics.cshtml"));

        Assert.Contains("_HighAccuracyStatusSummary", source);
        Assert.DoesNotContain("GetHighAccuracyStatusLabel", source);
    }
}
