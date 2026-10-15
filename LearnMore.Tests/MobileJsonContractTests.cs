using System.Text.Json;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class MobileJsonContractTests
{
    // 網站保留原始欄位名稱，App 的 API 仍須使用 camelCase。
    private static JsonElement Serialize(object value) => JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions { PropertyNamingPolicy = null });

    [Fact]
    public void SongDetailUsesThePropertyNamesExpectedByTheApp()
    {
        var song = new MobileLibraryService.Song("test-song", "歌名", "歌手", "演唱者", null);
        var line = new MobileLibraryService.Line(1, 2.5, "歌詞", "<ruby>歌詞</ruby>", "kashi", "歌詞");
        var detail = Serialize(new MobileLibraryService.Detail(song, [line]));

        Assert.Equal(["song", "lyrics"], detail.EnumerateObject().Select(p => p.Name));
        var songJson = detail.GetProperty("song");
        Assert.Equal(["songUid", "title", "artist", "performer", "videoId"],
            songJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal("test-song", songJson.GetProperty("songUid").GetString());
        Assert.Equal(JsonValueKind.Null, songJson.GetProperty("videoId").ValueKind);

        var lineJson = detail.GetProperty("lyrics")[0];
        Assert.Equal(["id", "time", "japanese", "ruby", "roman", "chinese"],
            lineJson.EnumerateObject().Select(p => p.Name));
        Assert.Equal(2.5, lineJson.GetProperty("time").GetDouble());
    }

    [Fact]
    public void LoginUserUsesThePropertyNamesExpectedByTheApp()
    {
        var user = Serialize(new MobileSessionService.User(42, "測試者"));
        Assert.Equal(["id", "name"], user.EnumerateObject().Select(p => p.Name));
        Assert.Equal(42, user.GetProperty("id").GetInt32());
        Assert.Equal("測試者", user.GetProperty("name").GetString());
    }
}
