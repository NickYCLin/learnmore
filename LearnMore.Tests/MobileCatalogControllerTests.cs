using System.Text.Json;
using LearnMore.Controllers.API;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public class MobileCatalogControllerTests
{
    private static MobileCatalogController Create() => new(new ConfigurationBuilder().Build());

    [Theory]
    [InlineData("abc]; DROP TABLE Songs;--")]
    [InlineData("abc\n")]
    [InlineData("../Songs")]
    [InlineData("")]
    public async Task InvalidIdentifiersAreRejectedBeforeDatabaseAccess(string uid)
    {
        Assert.IsType<BadRequestObjectResult>(await Create().Lyrics(uid));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10001, 30)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task PaginationCannotOverflowOrRequestUnboundedResults(int page, int pageSize)
    {
        Assert.IsType<BadRequestObjectResult>(await Create().List(page: page, pageSize: pageSize));
    }

    [Fact]
    public async Task OversizedSearchIsRejectedBeforeDatabaseAccess()
    {
        Assert.IsType<BadRequestObjectResult>(await Create().List(q: new string('a', 201)));
    }

    [Fact]
    public void WireContractMatchesSwiftDecodersDespiteGlobalMvcNamingPolicy()
    {
        var page = new MobileSongPage([new("uid", "曲", "歌手", "https://example.com/image.png", "https://youtube.com/watch?v=OHAjc-ayhus")], true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page));
        Assert.True(json.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal("uid", json.RootElement.GetProperty("songs")[0].GetProperty("id").GetString());
        Assert.Equal("https://example.com/image.png", json.RootElement.GetProperty("songs")[0].GetProperty("thumbnailURL").GetString());
        Assert.Equal("https://youtube.com/watch?v=OHAjc-ayhus", json.RootElement.GetProperty("songs")[0].GetProperty("videoURL").GetString());
        using var lyric = JsonDocument.Parse(JsonSerializer.Serialize(new MobileLyric(1, 1.5, "日", "中", "roman")));
        Assert.Equal(1.5, lyric.RootElement.GetProperty("seconds").GetDouble());
        Assert.Equal("日", lyric.RootElement.GetProperty("japanese").GetString());
    }
}
