using Xunit;

namespace LearnMore.Tests;

public class MediaManageStatusSurfaceTests
{
    [Fact]
    public void ManageView_ShouldRenderActionableHighAccuracyStatusBadgeViaSharedPartial()
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

        Assert.Contains("_HighAccuracyStatusSummary", source);
        Assert.Contains("bg-warning-subtle text-warning-emphasis", partialSource);
        Assert.Contains("bg-info-subtle text-info-emphasis", partialSource);
        Assert.DoesNotContain("bg-success-subtle text-success-emphasis", partialSource);
    }
}
