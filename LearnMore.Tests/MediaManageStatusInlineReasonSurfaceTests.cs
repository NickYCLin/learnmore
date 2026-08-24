using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class MediaManageStatusInlineReasonSurfaceTests
{
    [Fact]
    public void ManageView_ShouldUseSharedHighAccuracyStatusPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Manage.cshtml"));

        Assert.Contains("_HighAccuracyStatusSummary", source);
        Assert.DoesNotContain("GetHighAccuracyStatusLabel", source);
        Assert.DoesNotContain("GetHighAccuracyReasonText", source);
    }
}
