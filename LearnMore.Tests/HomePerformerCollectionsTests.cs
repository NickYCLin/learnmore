using System;
using System.IO;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public sealed class HomePerformerCollectionsTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void HomeAllPage_BuildsAutomaticPerformerCollectionsAboveFiveSongs()
    {
        var controller = Source("LearnMore", "Controllers", "HomeController.cs");
        var model = Source("LearnMore", "Models", "PerformerCollectionViewModel.cs");
        var groupPlayer = Source("LearnMore", "Controllers", "GroupPlayerController.cs");
        var home = Source("LearnMore", "Views", "Home", "Index.cshtml");
        var homeCss = Source("LearnMore", "wwwroot", "css", "home.css");

        Assert.Contains("string? performer = null", controller);
        Assert.Contains("BuildPerformerCollections", controller);
        Assert.Contains("SongCount > 5", controller);
        Assert.Contains("ViewBag.PerformerCollections", controller);
        Assert.Contains("SelectPerformerThumbnailUrl", controller);
        Assert.Contains("ChannelThumbnailUrl", controller);
        Assert.Contains("SelectedPerformer", controller);
        Assert.Contains("public sealed class PerformerCollectionViewModel", model);
        Assert.Contains("public string Performer", model);
        Assert.Contains("public int SongCount", model);
        Assert.Contains("performer-collection-section", home);
        Assert.Contains("演唱者合輯", home);
        Assert.Contains("asp-route-performer", home);
        Assert.Contains("asp-action=\"PlayPerformer\"", home);
        Assert.Contains("[HttpGet(\"Performer\")]", groupPlayer);
        Assert.Contains("PlayPerformer([FromQuery] string performer)", groupPlayer);
        Assert.Contains("GroupUid = \"performer:\" + canonicalPerformer", groupPlayer);
        Assert.Contains("PerformerNameNormalizer.NormalizeForCollection", controller);
        Assert.Contains("PerformerNameNormalizer.NormalizeForCollection", groupPlayer);
        Assert.Contains("PerformerNameNormalizer.GetCollectionAliases", groupPlayer);
        Assert.Contains("LTRIM(RTRIM(s.Performer)) = @Performer{index}", groupPlayer);
        Assert.Contains("ORDER BY s.SongID DESC", groupPlayer);
        Assert.Contains("collection.SongCount", home);
        Assert.Contains("performer-collection-actions", home);
        Assert.Contains("performer-collection-prev", home);
        Assert.Contains("performer-collection-next", home);
        Assert.Contains("scroll-snap-type: x mandatory", homeCss);
        Assert.Contains("scrollByStep", home);
        Assert.Contains("handleNavClick", home);
        Assert.Contains("button.disabled", home);
        Assert.Contains("!hasOverflow()", home);
        Assert.Contains("initializeInfiniteLoop", home);
        Assert.Contains("performer-collection-card-clone-before", home);
        Assert.Contains("performer-collection-card-clone-after", home);
        Assert.Contains("normalizeInfiniteScroll", home);
        Assert.Contains("prepareInfiniteScrollForDirection", home);
        Assert.Contains("left <= edgeBuffer", home);
        Assert.Contains("jumpToScrollLeft", home);
        Assert.Contains("performer-collection-list-infinite", homeCss);
        Assert.Contains("performer-collection-list-jump", homeCss);
        Assert.Contains("scroll-behavior: auto !important", homeCss);
        Assert.Contains("scrollbar-width: none", homeCss);
        Assert.Contains("border-radius: 50%", homeCss);
        Assert.Contains("resumeAutoAt = Date.now() + 8000", home);
        Assert.Contains("window.setInterval", home);
    }

    [Fact]
    public void HomeHeader_UsesSiteWideSongCountForAllFilters()
    {
        var controller = Source("LearnMore", "Controllers", "HomeController.cs");
        var home = Source("LearnMore", "Views", "Home", "Index.cshtml");

        Assert.Contains("totalSongs = await GetCachedHomeSongsCountAsync(conn);", controller);
        Assert.DoesNotContain("if (shouldPageHomeAll)\r\n                    {\r\n                        totalSongs = await GetCachedHomeSongsCountAsync(conn);", controller);
        Assert.DoesNotContain("if (shouldPageHomeAll)\n                    {\n                        totalSongs = await GetCachedHomeSongsCountAsync(conn);", controller);
        Assert.Contains("ViewBag.TotalSongs = await GetCachedHomeSongsCountAsync(conn);", controller);
        Assert.Contains("\"home:songs:count\"", controller);
        Assert.Contains("收錄 @totalSongs 首日文歌曲", home);
        Assert.Contains("ViewData[\"Description\"]", home);
    }

    [Fact]
    public void HomeAllPage_PaginationProvidesDirectPageNavigation()
    {
        var controller = Source("LearnMore", "Controllers", "HomeController.cs");
        var home = Source("LearnMore", "Views", "Home", "Index.cshtml");
        var homeCss = Source("LearnMore", "wwwroot", "css", "home.css");

        Assert.Contains("ViewBag.TotalPages", controller);
        Assert.Contains("RedirectToAction(nameof(Index), new { type, page = totalPages })", controller);
        Assert.Contains("home-pagination-summary", home);
        Assert.Contains("home-pagination-controls", home);
        Assert.Contains("home-pagination-pages", home);
        Assert.Contains("第一頁", home);
        Assert.Contains("最後頁", home);
        Assert.Contains("aria-current=\"page\"", home);
        Assert.Contains("home-pagination-number active", home);
        Assert.Contains("overflow-x: auto", homeCss);
        Assert.Contains("home-pagination-edge span", homeCss);
        Assert.Contains("align-items: center", homeCss);
        Assert.Contains("justify-content: center", homeCss);
    }

    [Fact]
    public void PerformerCollections_NormalizeKenshiYonezuRomanizedAlias()
    {
        Assert.Equal("米津玄師", PerformerNameNormalizer.NormalizeForCollection("米津玄師 Kenshi Yonezu"));
        Assert.Equal("米津玄師", PerformerNameNormalizer.NormalizeForCollection("  米津玄師   Kenshi Yonezu  "));
        Assert.Contains("米津玄師", PerformerNameNormalizer.GetCollectionAliases("米津玄師"));
        Assert.Contains("米津玄師 Kenshi Yonezu", PerformerNameNormalizer.GetCollectionAliases("米津玄師"));
    }

    [Fact]
    public void PerformerCollections_NormalizeTukiAgeSuffixAlias()
    {
        Assert.Equal("tuki.", PerformerNameNormalizer.NormalizeForCollection("tuki.(17)"));
        Assert.Equal("tuki.", PerformerNameNormalizer.NormalizeForCollection("  tuki.(17)  "));
        Assert.Contains("tuki.", PerformerNameNormalizer.GetCollectionAliases("tuki."));
        Assert.Contains("tuki.(17)", PerformerNameNormalizer.GetCollectionAliases("tuki."));
    }
}
