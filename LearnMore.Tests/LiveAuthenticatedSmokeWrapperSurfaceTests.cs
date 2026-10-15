using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class LiveAuthenticatedSmokeWrapperSurfaceTests
{
    [Fact]
    public void SmokeWrapper_ShouldExist_AndInvokePythonScriptWithSafeDefaults()
    {
        var wrapperPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "scripts",
            "run_learnmore_live_authenticated_smoke.sh");

        Assert.True(File.Exists(wrapperPath), $"Smoke wrapper not found: {wrapperPath}");

        var source = File.ReadAllText(wrapperPath);
        Assert.Contains("learnmore_live_authenticated_smoke.py", source);
        Assert.Contains("--repair-test-login", source);
        Assert.Contains("--borrow-song-if-empty", source);
    }
}
