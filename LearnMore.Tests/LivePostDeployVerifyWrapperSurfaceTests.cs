using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class LivePostDeployVerifyWrapperSurfaceTests
{
    [Fact]
    public void PostDeployVerifyWrapper_ShouldExist_AndRunHealthCheckBeforeSmoke()
    {
        var wrapperPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "scripts",
            "verify_learnmore_post_deploy.sh");

        Assert.True(File.Exists(wrapperPath), $"Post-deploy wrapper not found: {wrapperPath}");

        var source = File.ReadAllText(wrapperPath);
        Assert.Contains("curl --http2 -L --max-time 45 -sS -D - -o /dev/null", source);
        Assert.Contains("run_learnmore_live_authenticated_smoke.sh", source);
        Assert.True(source.IndexOf("curl --http2 -L --max-time 45 -sS -D - -o /dev/null", StringComparison.Ordinal) <
                    source.IndexOf("run_learnmore_live_authenticated_smoke.sh", StringComparison.Ordinal));
    }
}
