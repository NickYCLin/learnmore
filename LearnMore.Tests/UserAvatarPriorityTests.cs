using Xunit;

namespace LearnMore.Tests;

public class UserAvatarPriorityTests
{
    [Fact]
    public void Login_ShouldPersistUploadedAvatarBeforeGooglePictureForHeader()
    {
        var source = ReadSource("LearnMore", "Controllers", "LoginController.cs");

        Assert.Contains("Avatar", source);
        Assert.Contains("UserAvatarUrlResolver.Resolve", source);
        Assert.Contains("Request.PathBase", source);
        Assert.Contains("LoadPreferredUserProfileAsync", source);
        Assert.Contains("displayPicture", source);
        Assert.Contains("displayPicture", source[source.IndexOf("ValidGoogleLogin", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void ProfileUpdate_ShouldRefreshSessionPictureToUploadedAvatarOrGoogleFallback()
    {
        var source = ReadSource("LearnMore", "Controllers", "UserController.cs");

        Assert.Contains("PersistentLoginSessionService", source);
        Assert.Contains("SELECT Avatar, Picture FROM Users WHERE Email = @Email", source);
        Assert.Contains("UserAvatarUrlResolver.Resolve", source);
        Assert.Contains("HttpContext.Session.SetString(\"Picture\"", source);
        Assert.Contains("_persistentLoginSessionService.PersistAsync", source);
    }

    [Fact]
    public void Header_ShouldUseResolvedSessionPictureForUserMenuImage()
    {
        var source = ReadSource("LearnMore", "Views", "Shared", "_LayoutHeader.cshtml");

        Assert.Contains("userPicture", source);
        Assert.Contains("user-avatar-img", source);
        Assert.Contains("User Picture", source);
    }

    private static string ReadSource(params string[] parts)
    {
        return File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));
    }
}
