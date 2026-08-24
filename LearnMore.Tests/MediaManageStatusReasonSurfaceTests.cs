using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class MediaManageStatusReasonSurfaceTests
{
    [Fact]
    public void ManageView_ShouldPassFailureReasonIntoSharedPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Manage.cshtml"));

        var partialSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_HighAccuracyStatusSummary.cshtml"));

        Assert.Contains("BadgeTitle = song.HighAccuracyStatusReason", source);
        Assert.Contains("title=\"@Model.BadgeTitle\"", partialSource);
    }
}
