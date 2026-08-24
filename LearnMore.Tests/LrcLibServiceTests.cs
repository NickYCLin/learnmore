using System.Collections.Generic;
using System.Text.Json;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class LrcLibServiceTests
{
    [Fact]
    public void SelectBestSyncedLyricsCandidate_ShouldPreferLaterFirstTimestampWhenTextShapeMatches()
    {
        var candidates = JsonSerializer.Deserialize<List<JsonElement>>("""
        [
          {
            "trackName": "残酷な天使のテーゼ",
            "artistName": "高橋洋子",
            "syncedLyrics": "[00:00.90] 残酷な天使のように\n[00:07.28] 少年よ神話になれ\n[00:23.17] 蒼い風がいま胸のドアを叩いても\n[00:30.02] 私だけをただ見つめて微笑んでるあなた\n[00:37.33] そっとふれるものもとめることに夢中で"
          },
          {
            "trackName": "残酷な天使のテーゼ",
            "artistName": "高橋洋子",
            "syncedLyrics": "[00:02.39] 残酷な天使のように\n[00:08.53] 少年よ神話になれ\n[00:23.97] 蒼い風がいま胸のドアを叩いても\n[00:30.76] 私だけをただ見つめて微笑んでるあなた\n[00:38.55] そっとふれるものもとめることに夢中で"
          }
        ]
        """)!;

        var lines = LrcLibService.SelectBestSyncedLyricsCandidate(candidates);

        Assert.NotNull(lines);
        Assert.Equal(2.39, lines![0].TimeStamp, 2);
        Assert.Equal("残酷な天使のように", lines[0].Japanese);
    }

    [Fact]
    public void SelectBestSyncedLyricsCandidate_ShouldRejectSameArtistWrongSong()
    {
        var candidates = JsonSerializer.Deserialize<List<JsonElement>>("""
        [
          {
            "trackName": "東京フラッシュ",
            "artistName": "Vaundy",
            "syncedLyrics": "[00:22.00] 相槌が上手くなったんだ\n[00:27.00] できてる できてる\n[00:30.00] あぁ 君もうまいね\n[00:32.00] 合図なしで攻撃してきたんだ\n[00:37.00] 悪くない 悪くない"
          },
          {
            "trackName": "呼び声",
            "artistName": "Vaundy",
            "syncedLyrics": "[00:01.13] この惑星の真ん中で\n[00:08.66] 時折り描いた暗闇照らす何か\n[00:16.70] それは紅色の記憶のような\n[00:24.87] 空いた穴を埋めていくような何か\n[00:32.68] （この夢が覚めたら）"
          }
        ]
        """)!;

        var lines = LrcLibService.SelectBestSyncedLyricsCandidate(
            candidates,
            "呼び声(NHK総合「Vaundy 18祭」テーマソング)",
            "Vaundy");

        Assert.NotNull(lines);
        Assert.Equal("この惑星の真ん中で", lines![0].Japanese);
    }

    [Fact]
    public void SelectBestSyncedLyricsCandidate_ShouldPreferDurationMatchedOfficialVideo()
    {
        var candidates = JsonSerializer.Deserialize<List<JsonElement>>("""
        [
          {
            "trackName": "Nan-Nan",
            "artistName": "Fujii Kaze",
            "duration": 321.0,
            "syncedLyrics": "[01:03.90] あんたのその歯に はさがった青さ粉に\n[01:08.99] ふれるべきか否かで少し悩んでる\n[01:11.60] 口にしない方がいい真実もあるから\n[01:13.29] 知らない方が良かったなんて言わないで居て\n[01:14.56] 何があってもずっと大好きなのに"
          },
          {
            "trackName": "Fujii Kaze - Nan-Nan (Official Video)",
            "artistName": "Fujii Kaze",
            "duration": 327.3675,
            "syncedLyrics": "[01:02.81] あんたのその歯に はさがった青さ粉に\n[01:08.91] ふれるべきか否かで少し悩んでる\n[01:14.51] 口にしない方がいい真実もあるから\n[01:24.22] 知らない方が良かったなんて言わないで居て\n[01:30.53] 何があってもずっと大好きなのに"
          }
        ]
        """)!;

        var lines = LrcLibService.SelectBestSyncedLyricsCandidate(
            candidates,
            "Nan-Nan",
            "Fujii Kaze",
            durationSeconds: 327);

        Assert.NotNull(lines);
        Assert.Equal(62.81, lines![0].TimeStamp, 2);
    }
}
