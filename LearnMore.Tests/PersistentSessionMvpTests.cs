using Xunit;

namespace LearnMore.Tests;

public class PersistentSessionMvpTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void Program_ShouldPersistDataProtectionKeysForLoginCookieAcrossDeploys()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "LearnMore", "Program.cs"));

        Assert.Contains("AddDataProtection", source);
        Assert.Contains("PersistKeysToFileSystem", source);
        Assert.Contains("SetApplicationName(\"LearnMore\")", source);
        Assert.Contains("DataProtectionKeys", source);
    }

    [Fact]
    public void Program_ShouldRestoreSessionFromPersistentCookieBeforeRoutes()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "LearnMore", "Program.cs"));

        Assert.Contains("AddScoped<PersistentLoginSessionService>", source);
        Assert.Contains("RestoreSessionAsync", source);

        var useSession = source.IndexOf("app.UseSession();", StringComparison.Ordinal);
        var restore = source.IndexOf("RestoreSessionAsync", StringComparison.Ordinal);
        var routing = source.IndexOf("app.UseRouting();", StringComparison.Ordinal);

        Assert.True(useSession >= 0, "UseSession should be configured.");
        Assert.True(restore > useSession, "Persistent cookie restore should run after UseSession.");
        Assert.True(routing > restore, "Persistent cookie restore should run before route handlers read Session.");
    }

    [Fact]
    public void LoginController_ShouldWritePersistentCookieOnLoginAndDeleteItOnLogout()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "LearnMore", "Controllers", "LoginController.cs"));

        Assert.Contains("PersistentLoginSessionService", source);
        Assert.Contains("PersistAsync", source);
        Assert.Contains("ClearPersistentCookie", source);
    }

    [Fact]
    public void PersistentLoginSessionService_ShouldUseProtectedHttpOnlyEssentialCookie()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "LearnMore", "Services", "PersistentLoginSessionService.cs"));

        Assert.Contains("LearnMore.PersistentSession", source);
        Assert.Contains("IDataProtector", source);
        Assert.Contains("CookieOptions", source);
        Assert.Contains("HttpOnly = true", source);
        Assert.Contains("IsEssential = true", source);
        Assert.Contains("SameSite = SameSiteMode.Lax", source);
        Assert.Contains("Secure = httpContext.Request.IsHttps", source);
    }

    [Fact]
    public void PersistentLoginSessionService_ShouldRenewPersistentCookieWhenUserKeepsUsingSite()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "LearnMore", "Services", "PersistentLoginSessionService.cs"));

        Assert.Contains("RenewPersistentCookie", source);
        Assert.Contains("RenewPersistentCookie(httpContext, payload)", source);
        Assert.Contains("RenewPersistentCookie(httpContext, new PersistentLoginSessionPayload", source);
    }
}
