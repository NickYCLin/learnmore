using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class PrivacyAndCommentSecurityTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void PrivacyRoute_ShouldRedirectToUserAboutAndLayoutsShouldLinkThere()
    {
        var controller = Source("LearnMore", "Controllers", "HomeController.cs");
        var layout = Source("LearnMore", "Views", "Shared", "_Layout.cshtml");
        var loginLayout = Source("LearnMore", "Views", "Shared", "_LayoutLogin.cshtml");

        Assert.Contains("return RedirectToAction(\"About\", \"User\");", controller);
        Assert.Contains("asp-controller=\"User\" asp-action=\"About\"", layout);
        Assert.Contains("asp-controller=\"User\" asp-action=\"About\"", loginLayout);
        Assert.DoesNotContain("asp-controller=\"Home\" asp-action=\"Privacy\"", layout);
        Assert.DoesNotContain("asp-controller=\"Home\" asp-action=\"Privacy\"", loginLayout);
    }

    [Fact]
    public void AddComment_ShouldRejectAnonymousPrivateCommentsServerSide()
    {
        var controller = Source("LearnMore", "Controllers", "LyricsController.cs");

        Assert.Contains("model.IsPrivate && string.IsNullOrWhiteSpace(UserEmail)", controller);
        Assert.Contains("登入後才能使用私密留言", controller);
        Assert.True(
            controller.IndexOf("model.IsPrivate && string.IsNullOrWhiteSpace(UserEmail)", StringComparison.Ordinal)
            < controller.IndexOf("INSERT INTO Comments", StringComparison.Ordinal),
            "Private comment authorization must run before inserting the comment.");
    }

    [Fact]
    public void LyricsPrivateComment_ShouldPromptLoginInsteadOfSilentlyDisablingCheckbox()
    {
        var lyricsView = Source("LearnMore", "Views", "Lyrics", "Index.cshtml");

        Assert.Contains("data-login-required=", lyricsView);
        Assert.Contains("aria-disabled=", lyricsView);
        Assert.Contains("alert(\"登入後才能使用私密留言唷！\")", lyricsView);
        Assert.Contains("privateCommentCheckbox?.dataset.loginRequired === \"true\"", lyricsView);
        Assert.DoesNotContain("id=\"private-comment\" @(isLoggedIn ? \"\" : \"disabled\")", lyricsView);
        Assert.DoesNotContain("showMessage(\"登入後才能使用私密留言唷！\")", lyricsView);
    }

    [Fact]
    public void MediaDeleteSong_ShouldRequireSongOwnerOrManagerServerSide()
    {
        var controller = Source("LearnMore", "Controllers", "API", "MediaApiController.cs");

        Assert.Contains("CanDeleteSongAsync", controller);
        Assert.Contains("ISNULL(U.Manager, 0) AS Manager", controller);
        Assert.Contains("S.AddedByUserId", controller);
        Assert.Contains("ISNULL(U.Producer, '') AS Producer", controller);
        Assert.Contains("return Forbid();", controller);
        Assert.True(
            controller.IndexOf("CanDeleteSongAsync", StringComparison.Ordinal)
            < controller.IndexOf("DROP TABLE", StringComparison.Ordinal),
            "Delete authorization must run before dropping the dynamic lyrics table.");
    }

    [Fact]
    public void DynamicSongTableEndpoints_ShouldValidateSongUidBeforeUsingTableName()
    {
        var lyricsController = Source("LearnMore", "Controllers", "LyricsController.cs");
        var groupPlayerController = Source("LearnMore", "Controllers", "GroupPlayerController.cs");

        Assert.Contains("SafeSongUidPattern", lyricsController);
        Assert.Contains("!SafeSongUidPattern.IsMatch(songUid)", lyricsController);
        Assert.Contains("!SafeSongUidPattern.IsMatch(model.SongUid)", lyricsController);
        Assert.Contains("SafeSongUidPattern", groupPlayerController);
        Assert.Contains("!SafeSongUidPattern.IsMatch(songUid)", groupPlayerController);
        Assert.True(
            groupPlayerController.IndexOf("!SafeSongUidPattern.IsMatch(songUid)", StringComparison.Ordinal)
            < groupPlayerController.IndexOf("$\"[Songs_{songUid}]\"", StringComparison.Ordinal),
            "GroupPlayer must validate songUid before composing the dynamic lyrics table name.");
    }
}
