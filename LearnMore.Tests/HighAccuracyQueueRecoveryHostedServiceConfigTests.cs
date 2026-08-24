using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class HighAccuracyQueueRecoveryHostedServiceConfigTests
{
    [Fact]
    public void RecoveryHostedService_ShouldSupportDisablingAutomaticQueueRecovery()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "HighAccuracyQueueRecoveryHostedService.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("HighAccuracyQueueRecoveryEnabled", source);
        Assert.Contains("queue-recovery:disabled", source);
    }

    [Fact]
    public void AppSettings_ShouldKeepQueueRecoveryEnabledByDefault()
    {
        var appsettingsPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "appsettings.json");

        var source = File.ReadAllText(appsettingsPath);

        Assert.Contains("\"HighAccuracyQueueRecoveryEnabled\": true", source);
    }
}
