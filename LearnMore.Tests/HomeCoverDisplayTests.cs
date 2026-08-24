using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class HomeCoverDisplayTests
{
    private static string HomeIndexSource()
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore", "Views", "Home", "Index.cshtml"));
    }

    [Fact]
    public void HomeSongCards_ShowPerformerAndOriginalButDoNotExposeCover()
    {
        var source = HomeIndexSource();

        Assert.Contains("data-performer=\"@song.Performer\"", source);
        Assert.Contains("home-song-performer", source);
        Assert.Contains("<span class=\"song-meta-label\">演唱者</span>", source);
        Assert.Contains("<span class=\"song-meta-label\">原唱</span>", source);
        Assert.DoesNotContain("data-cover=\"@song.Cover\"", source);
        Assert.DoesNotContain("home-song-cover", source);
        Assert.DoesNotContain("<span class=\"song-meta-label\">Cover</span>", source);
        Assert.DoesNotContain("card.dataset.cover", source);
    }
}
