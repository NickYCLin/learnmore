using System.Reflection;
using LearnMore.Controllers.API;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public class YoutubeControllerVideoIdTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=OHAjc-ayhus", "OHAjc-ayhus")]
    [InlineData("https://www.youtube.com/watch?v=OHAjc-ayhus&list=abc", "OHAjc-ayhus")]
    [InlineData("https://youtu.be/OHAjc-ayhus?si=D-yHfuxt_pcqmlqP", "OHAjc-ayhus")]
    [InlineData("https://www.youtube.com/embed/OHAjc-ayhus", "OHAjc-ayhus")]
    [InlineData("https://www.youtube-nocookie.com/embed/OHAjc-ayhus", "OHAjc-ayhus")]
    [InlineData("https://www.youtube.com/shorts/OHAjc-ayhus", "OHAjc-ayhus")]
    [InlineData("https://www.youtube.com/live/OHAjc-ayhus?feature=share", "OHAjc-ayhus")]
    [InlineData("OHAjc-ayhus", "OHAjc-ayhus")]
    public void ExtractVideoIdSupportsCommonYoutubeUrlForms(string url, string expected)
    {
        Assert.Equal(expected, ExtractVideoId(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/watch?v=OHAjc-ayhus")]
    [InlineData("https://www.youtube.com/watch?v=too-short")]
    [InlineData("https://youtu.be/too-short")]
    public void ExtractVideoIdRejectsInvalidValues(string url)
    {
        Assert.Null(ExtractVideoId(url));
    }

    private static string? ExtractVideoId(string url)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=(local);Database=LearnMore;Trusted_Connection=True;",
                ["YouTube:ApiKey"] = "test-key"
            })
            .Build();
        var controller = new YoutubeController(configuration);
        var method = typeof(YoutubeController).GetMethod("ExtractVideoId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(YoutubeController), "ExtractVideoId");
        return (string?)method.Invoke(controller, [url]);
    }
}
