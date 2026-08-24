using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LearnMore.Tests;

public class FavoritesFeatureTests
{
    [Fact]
    public void FavoritesAction_ShouldRequireLoginAndQueryOnlyOwnedGroups()
    {
        var source = File.ReadAllText(ResolveProjectPath("LearnMore/Controllers/UserController.cs"));

        Assert.Contains("public async Task<IActionResult> Favorites()", source);
        Assert.Contains("Session.GetString(\"UserId\")", source);
        Assert.Contains("RedirectToAction(\"Index\", \"Login\")", source);
        Assert.Contains("INNER JOIN [language].[dbo].[SongGroup] G", source);
        Assert.Contains("INNER JOIN [language].[dbo].[Songs] S", source);
        Assert.Contains("WHERE G.UserId = @UserId", source);
        Assert.Contains("command.Parameters.Add(\"@UserId\"", source);
        Assert.Contains("new FavoriteSongViewModel", source);
    }

    [Fact]
    public void FavoritesView_ShouldRenderSongsGroupsAndEmptyState()
    {
        var source = File.ReadAllText(ResolveProjectPath("LearnMore/Views/User/Favorites.cshtml"));

        Assert.Contains("@model IReadOnlyList<LearnMore.Models.FavoriteSongViewModel>", source);
        Assert.Contains("data-favorite-card=\"true\"", source);
        Assert.Contains("favorite-group-chip", source);
        Assert.Contains("id=\"favorites-empty\"", source);
        Assert.Contains("管理收藏群組", source);
        Assert.Contains("~/js/home.js", source);
        Assert.DoesNotContain("For more information on enabling MVC", source);
    }

    [Fact]
    public void Header_ShouldExposeFavoritesOnDesktopAndMobile()
    {
        var source = File.ReadAllText(ResolveProjectPath("LearnMore/Views/Shared/_LayoutHeader.cshtml"));

        Assert.True(Regex.Matches(source, "Url.Action\\(\"Favorites\",\"User\"\\)").Count >= 2);
        Assert.True(Regex.Matches(source, "我的收藏").Count >= 2);
    }

    [Fact]
    public void MiniHeart_ShouldOpenPickerAndRefreshMembershipAfterRemoval()
    {
        var source = File.ReadAllText(ResolveProjectPath("LearnMore/wwwroot/js/home.js"));
        var handlerStart = source.IndexOf("const btn = e.target.closest('.mini-heart-btn');", StringComparison.Ordinal);
        var showPreview = source.IndexOf("showPreview(card, preview);", handlerStart, StringComparison.Ordinal);
        var expandPanel = source.IndexOf("expandGroupPanel(preview, songUid);", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0);
        Assert.True(showPreview > handlerStart);
        Assert.True(expandPanel > showPreview);
        Assert.Contains("preview.classList.add('group-picker-open')", source);
        Assert.Contains("await loadJoinedUids();", source);
        Assert.Contains("const remainsJoined = joinedUids.has(songUid);", source);
    }

    [Fact]
    public void HomeCss_ShouldUseValidResponsiveMediaRules()
    {
        var source = File.ReadAllText(ResolveProjectPath("LearnMore/wwwroot/css/home.css"));

        Assert.DoesNotContain("@@media", source);
        Assert.Contains("@media (max-width: 480px)", source);
        Assert.Contains(".hover-card-preview.group-picker-open", source);
    }

    private static string ResolveProjectPath(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not resolve project file: {relativePath}");
    }
}
