using System.Text.Json;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class NetEaseLrcServiceTests
{
    [Theory]
    [InlineData("作词 : 米津玄師")]
    [InlineData("作詞：米津玄師")]
    [InlineData("作曲 : 米津玄師")]
    [InlineData("编曲 : 米津玄師")]
    [InlineData("編曲：米津玄師")]
    [InlineData("制作人 : n-buna")]
    [InlineData("製作人：n-buna")]
    [InlineData("producer: n-buna")]
    public void ShouldSkipSyncedLyricLine_ShouldDropCreditMetadataLines(string text)
    {
        Assert.True(LyricLineFilter.ShouldSkipSyncedLyricLine(text));
    }

    [Theory]
    [InlineData("音楽")]
    [InlineData("音樂")]
    [InlineData("[音楽]")]
    [InlineData("【音楽】")]
    [InlineData("[Music]")]
    [InlineData("music")]
    [InlineData("歌")]
    [InlineData("【歌】")]
    [InlineData("唄")]
    [InlineData("（唄）")]
    [InlineData("拍手")]
    [InlineData("[歓声]")]
    public void ShouldSkipSyncedLyricLine_ShouldDropNonLyricNoiseLines(string text)
    {
        Assert.True(LyricLineFilter.ShouldSkipSyncedLyricLine(text));
    }

    [Theory]
    [InlineData("サブタイトル・フォロー・インスタグラム")]
    [InlineData("字幕・追蹤・Instagram")]
    [InlineData("Subtitles / Follow / Instagram")]
    public void ShouldSkipSyncedLyricLine_ShouldDropSubtitleSocialPromptLines(string text)
    {
        Assert.True(LyricLineFilter.ShouldSkipSyncedLyricLine(text));
    }

    [Theory]
    [InlineData("街の灯りが消えるころ")]
    [InlineData("夢ならばどれほどよかったでしょう")]
    [InlineData("Music makes me high")]
    public void ShouldSkipSyncedLyricLine_ShouldKeepRealLyrics(string text)
    {
        Assert.False(LyricLineFilter.ShouldSkipSyncedLyricLine(text));
    }

    [Fact]
    public void ExtractPrimaryArtistName_ShouldReturnFirstArtistFromTopSong()
    {
        var json = """
        {
          "result": {
            "songs": [
              {
                "name": "残酷な天使のテーゼ",
                "artists": [
                  { "name": "高橋洋子" }
                ]
              }
            ]
          }
        }
        """;

        var artist = NetEaseLrcService.ExtractPrimaryArtistName(json);

        Assert.Equal("高橋洋子", artist);
    }

    [Fact]
    public void ExtractBestSongId_ShouldRejectSameArtistWrongSong()
    {
        const string json = """
        {
          "result": {
            "songs": [
              {
                "id": 100,
                "name": "東京フラッシュ",
                "artists": [{ "name": "Vaundy" }]
              },
              {
                "id": 200,
                "name": "呼び声",
                "artists": [{ "name": "Vaundy" }]
              }
            ]
          }
        }
        """;

        var songId = NetEaseLrcService.ExtractBestSongId(
            json,
            "呼び声(NHK総合「Vaundy 18祭」テーマソング)",
            "Vaundy");

        Assert.Equal(200, songId);
    }

    [Fact]
    public void BuildTitleCandidates_ShouldRemoveParentheticalTieInText()
    {
        var candidates = SyncedLyricsMetadataMatcher.BuildTitleCandidates(
            "呼び声(NHK総合「Vaundy 18祭」テーマソング)");

        Assert.Contains("呼び声", candidates);
    }
}
