using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class HomeNewSongsFilterTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void HomeNewSongs_FilterOnlyLastSevenDays()
    {
        var controller = Source("LearnMore", "Controllers", "HomeController.cs");

        Assert.Contains("WHERE AddedDate >= DATEADD(DAY, -7, GETDATE()) ORDER BY SongID DESC", controller);
        Assert.DoesNotContain("DATEADD(WEEK, -2, GETDATE())", controller);
    }
}
