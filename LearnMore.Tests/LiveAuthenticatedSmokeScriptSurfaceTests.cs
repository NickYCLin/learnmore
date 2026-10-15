using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class LiveAuthenticatedSmokeScriptSurfaceTests
{
    [Fact]
    public void LiveAuthenticatedSmokeScript_ShouldExist_WithCoreRoutes()
    {
        var scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "scripts",
            "learnmore_live_authenticated_smoke.py");

        Assert.True(File.Exists(scriptPath), $"Smoke script not found: {scriptPath}");

        var source = File.ReadAllText(scriptPath);
        Assert.Contains("/Login/TestLogin", source);
        Assert.Contains("X-LearnMore-Smoke-Token", source);
        Assert.Contains("TestAccount.SmokeToken", source);
        Assert.Contains("safe_command[2] = \"<redacted>\"", source);
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", source);
        Assert.DoesNotContain(".TestAccount | ConvertTo-Json", source);
        Assert.Contains("/Media/Manage", source);
        Assert.Contains("/EditLyrics/", source);
        Assert.Contains("mark_existing_song_for_smoke", source);
        Assert.Contains("StatusRestored", source);
        Assert.Contains("ReasonRestored", source);
        Assert.Contains("restore", source, StringComparison.OrdinalIgnoreCase);
    }
}
