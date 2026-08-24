using Xunit;

namespace LearnMore.Tests;

public class SearchSuggestionsMvpTests
{
    [Fact]
    public void HomeController_ShouldExposeSearchSuggestionsApiAndAliasBackedSearch()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "HomeController.cs"));

        Assert.Contains("api/search/suggestions", source);
        Assert.Contains("EnsureSongAliasesTableAsync", source);
        Assert.Contains("SongAliases", source);
        Assert.Contains("UX_SongAliases_SongUid_AliasText", source);
        Assert.Contains("AliasText", source);
        Assert.Contains("matchedBy", source);
        Assert.Contains("matchedText", source);
        Assert.Contains("ORDER BY MatchRank", source);
        Assert.DoesNotContain("SELECT TOP 8", source);
        Assert.Contains("BuildYouTubeThumbnailUrl", source);
        Assert.Contains("YouTubeVideoUrl", source);
        Assert.Contains("thumbnailUrl", source);
        Assert.Contains("img.youtube.com/vi", source);
    }

    [Fact]
    public void SongPersistence_ShouldIndexTraditionalChineseAndRomanizedTitleAliases()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperSongPersistenceService.cs"));

        Assert.Contains("InsertSearchAliasesAsync", source);
        Assert.Contains("traditional_chinese_title", source);
        Assert.Contains("romanized_title", source);
        Assert.Contains("auto_title_romanization", source);
        Assert.Contains("JapaneseRomanSanitizer.NormalizeWithContext", source);
    }

    [Fact]
    public void HeaderSearch_ShouldRenderAutocompleteDropdownWithKeyboardNavigation()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_LayoutHeader.cshtml"));

        Assert.Contains("search-suggestions", source);
        Assert.Contains("search-suggestion-item", source);
        Assert.Contains("/api/search/suggestions", source);
        Assert.Contains("setTimeout", source);
        Assert.Contains("ArrowDown", source);
        Assert.Contains("ArrowUp", source);
        Assert.Contains("Escape", source);
        Assert.Contains("data-search-url", source);
        Assert.Contains("item.thumbnailUrl || item.cover", source);
        Assert.Contains("addEventListener('error'", source);
        Assert.Contains("fa-music", source);
    }

    [Fact]
    public void HeaderSearch_ShouldHaveMobileRwdLayoutForInputAndSuggestions()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_LayoutHeader.cshtml"));

        Assert.Contains("data-search-rwd", source);
        Assert.Contains("search-surface", source);
        Assert.Contains("search-mobile-backdrop", source);
        Assert.Contains("search-suggestions-mobile-fixed", source);
        Assert.Contains("100dvw", source);
        Assert.Contains("max-height: min(62dvh", source);
        Assert.Contains("scrollIntoView({ block: 'nearest' })", source);
    }

    [Fact]
    public void HeaderSearch_ShouldStayOutsideCollapsedMobileMenu()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_LayoutHeader.cshtml"));

        var togglerIndex = source.IndexOf("navbar-toggler", StringComparison.Ordinal);
        var searchIndex = source.IndexOf("search-container search-container-stays-visible", StringComparison.Ordinal);
        var collapseIndex = source.IndexOf("collapse navbar-collapse", StringComparison.Ordinal);

        Assert.True(togglerIndex >= 0, "Header should render a navbar toggler.");
        Assert.True(searchIndex > togglerIndex, "The always-visible search box should render after the toggler in mobile source order.");
        Assert.True(collapseIndex > searchIndex, "The always-visible search box should render before the collapsed mobile menu.");
        Assert.Contains("search-container-stays-visible", source);
        Assert.Contains("flex-wrap: wrap", source);
        Assert.Contains("order: 2", source);
        Assert.Contains("max-height: calc(100dvh - 124px)", source);
    }
}
