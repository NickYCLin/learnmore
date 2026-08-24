using System;
using System.IO;
using Xunit;

namespace LearnMore.Tests;

public sealed class SongPerformerFieldTests
{
    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    [Fact]
    public void SongsSchema_IsExtendedWithNullablePerformerColumn()
    {
        var program = Source("LearnMore", "Program.cs");
        var persistence = Source("LearnMore", "Services", "WhisperSongPersistenceService.cs");

        Assert.Contains("COL_LENGTH('dbo.Songs', 'Performer') IS NULL", program);
        Assert.Contains("ALTER TABLE [language].[dbo].[Songs] ADD [Performer] NVARCHAR(255) NULL", program);
        Assert.Contains("EnsurePerformerColumnExistsAsync", persistence);
    }

    [Fact]
    public void SummonForm_SubmitsPerformerField()
    {
        var summon = Source("LearnMore", "Views", "Media", "Summon.cshtml");
        var model = Source("LearnMore", "Models", "SongViewModel.cs");
        var persistence = Source("LearnMore", "Services", "WhisperSongPersistenceService.cs");

        Assert.Contains("id=\"songPerformer\"", summon);
        Assert.Contains("演唱者", summon);
        Assert.Contains("SongPerformer: songPerformer", summon);
        Assert.Contains("public string SongPerformer", model);
        Assert.Contains("[Performer]", persistence);
        Assert.Contains("@Performer", persistence);
        Assert.Contains("ResolvePerformer(request.SongPerformer, request.SongCover, request.SongArtist)", persistence);
    }

    [Fact]
    public void Persistence_DefaultsBlankPerformerFromLegacyCoverThenArtist()
    {
        var persistence = Source("LearnMore", "Services", "WhisperSongPersistenceService.cs");
        var editMutation = Source("LearnMore", "Services", "WhisperEditMutationService.cs");

        Assert.Contains("ResolvePerformer(request.Performer, request.Cover, request.Artist)", persistence);
        Assert.Contains("ResolvePerformer(request.SongPerformer, request.SongCover, request.SongArtist)", persistence);
        Assert.Contains("ResolvePerformer(model.Song.Performer, null, model.Song.Artist)", editMutation);
        Assert.Contains("if (!string.IsNullOrWhiteSpace(cover))", persistence);
        Assert.Contains("return string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();", persistence);
        Assert.Contains("return string.IsNullOrWhiteSpace(artist) ? null : artist;", editMutation);
        Assert.DoesNotContain("[Cover]", persistence);
    }

    [Fact]
    public void PublicSongSurfaces_DisplayPerformerWhenPresent()
    {
        var home = Source("LearnMore", "Views", "Home", "Index.cshtml");
        var homeCss = Source("LearnMore", "wwwroot", "css", "home.css");
        var homeJs = Source("LearnMore", "wwwroot", "js", "home.js");
        var lyrics = Source("LearnMore", "Views", "Lyrics", "Index.cshtml");
        var groupPlayer = Source("LearnMore", "Views", "GroupPlayer", "Play.cshtml");
        var manage = Source("LearnMore", "Views", "Media", "Manage.cshtml");
        var edit = Source("LearnMore", "Views", "Media", "Edit.cshtml");

        Assert.Contains("data-performer=\"@song.Performer\"", home);
        Assert.Contains("home-song-performer", home);
        Assert.Contains("song-meta-stack", home);
        Assert.Contains("song-meta-line-primary", home);
        Assert.Contains("--preview-meta-h: 132px", homeCss);
        Assert.Contains("justify-content: flex-start", homeCss);
        Assert.Contains("meta.innerHTML = `<div class=\"title clamp-1\">", homeJs);
        Assert.True(homeJs.IndexOf("<span class=\"meta-label\">演唱者</span>", StringComparison.Ordinal) < homeJs.IndexOf("<span class=\"meta-label\">原唱</span>", StringComparison.Ordinal));
        Assert.Contains("card.dataset.performer", homeJs);
        Assert.True(home.IndexOf("!string.IsNullOrWhiteSpace(song.Performer)", StringComparison.Ordinal) < home.IndexOf("<span class=\"song-meta-label\">原唱</span>", StringComparison.Ordinal));
        Assert.Contains("<span class=\"song-meta-label\">原唱</span>", home);
        Assert.Contains("song-detail-meta-primary", lyrics);
        Assert.True(lyrics.IndexOf("Model.Performer", StringComparison.Ordinal) < lyrics.IndexOf("Model.Artist", StringComparison.Ordinal));
        Assert.DoesNotContain("Original:", home);
        Assert.Contains("<strong>演唱者</strong><span>@Model.Performer</span>", lyrics);
        Assert.DoesNotContain("shouldShowCover", lyrics);
        Assert.DoesNotContain("Model.Cover", lyrics);
        Assert.Contains("song.Performer", groupPlayer);
        Assert.Contains("@song.Performer", manage);
        Assert.Contains("asp-for=\"Song.Performer\"", edit);
        Assert.DoesNotContain("asp-for=\"Song.Cover\"", edit);
    }

    [Fact]
    public void GroupPlayerView_UsesUtf8TraditionalChineseLabels()
    {
        var groupPlayer = Source("LearnMore", "Views", "GroupPlayer", "Play.cshtml");

        Assert.Contains("播放：{Model.GroupName}", groupPlayer);
        Assert.Contains("點擊以開始播放", groupPlayer);
        Assert.Contains("下一首", groupPlayer);
        Assert.Contains("播放清單", groupPlayer);
        Assert.Contains("留言板", groupPlayer);
        Assert.Contains("輸入您的留言...", groupPlayer);
        Assert.Contains("私密留言", groupPlayer);
        Assert.Contains("送出留言", groupPlayer);
        Assert.Contains("自動同步歌詞", groupPlayer);
        Assert.Contains("顯示羅馬拼音", groupPlayer);
        Assert.Contains("目前沒有留言，快來留言吧！", groupPlayer);
        Assert.Contains("從此句開始播放", groupPlayer);
        Assert.Contains("暫無歌詞", groupPlayer);
        Assert.Contains("group-player-layout-container", groupPlayer);
        Assert.Contains("group-player-page-container", groupPlayer);
        Assert.Contains(".group-player-layout-container > main.pb-3", groupPlayer);
        Assert.Contains("width: auto !important;", groupPlayer);
        Assert.Contains("max-width: none !important;", groupPlayer);
        Assert.Contains("#playPauseBtn", groupPlayer);
        Assert.Contains("#prevBtn", groupPlayer);
        Assert.Contains("#nextBtn", groupPlayer);
        Assert.Contains("background: rgba(15, 23, 42, 0.22) !important;", groupPlayer);
        Assert.Contains("border-color: rgba(255, 255, 255, 0.86) !important;", groupPlayer);
        Assert.Contains("font-weight: 800;", groupPlayer);
        Assert.Contains("color: #fff !important;", groupPlayer);
        Assert.Contains("text-shadow: 0 1px 2px rgba(0, 0, 0, 0.35);", groupPlayer);
        Assert.Contains("margin-left: 0 !important;", groupPlayer);
        Assert.Contains("padding-left: clamp(6px, 0.8vw, 12px) !important;", groupPlayer);
        Assert.Contains("group-player-content-row", groupPlayer);
        Assert.Contains("col-md-4 col-lg-3 group-player-playlist-column", groupPlayer);
        Assert.Contains("col-md-8 col-lg-9 group-player-detail-column", groupPlayer);
        Assert.Contains("playlist-container group-player-playlist-right", groupPlayer);
        Assert.Contains("main-content group-player-main-content", groupPlayer);
        Assert.Contains("flex-direction: column;", groupPlayer);
        Assert.Contains(".group-player-main-content .video-section { width: 100%;", groupPlayer);
        Assert.Contains(".group-player-main-content .lyrics-card { width: 100%; min-width: 0; }", groupPlayer);
        Assert.Contains(".group-player-playlist-column { order: 2; }", groupPlayer);
        Assert.Contains(".group-player-detail-column { order: 1; }", groupPlayer);
        Assert.Contains(".playlist-container.group-player-playlist-right", groupPlayer);
        Assert.Contains("group-player-playlist-height-synced", groupPlayer);
        Assert.Contains("height: var(--group-player-detail-height);", groupPlayer);
        Assert.Contains("function syncPlaylistHeightToDetail()", groupPlayer);
        Assert.Contains("function watchPlaylistHeightToDetail()", groupPlayer);
        Assert.Contains("new ResizeObserver(syncPlaylistHeightToDetail)", groupPlayer);
        Assert.Contains("playlist.style.setProperty('--group-player-detail-height'", groupPlayer);
        Assert.Contains("watchPlaylistHeightToDetail();", groupPlayer);
        Assert.DoesNotContain("function syncPlaylistHeightToLyrics()", groupPlayer);
        Assert.DoesNotContain("group-player-playlist-synced", groupPlayer);
        Assert.True(groupPlayer.IndexOf("<div class=\"lyrics-card\">", StringComparison.Ordinal) < groupPlayer.IndexOf("<div class=\"comments-section\"><h3>", StringComparison.Ordinal));
        Assert.True(groupPlayer.IndexOf("<div class=\"col-md-8 col-lg-9 group-player-detail-column\">", StringComparison.Ordinal) < groupPlayer.IndexOf("<div class=\"col-md-4 col-lg-3 group-player-playlist-column\">", StringComparison.Ordinal));

        var mojibakeMarkers = new[]
        {
            "¡G",
            "ÂIÀ»",
            "播放²M³æ",
            "下@首",
            "¯d¨¥",
            "¿é¤J",
            "°e¥X",
            "¦Û°Ê",
            "Åã¥Ü",
            "¥Ø«e",
            "¼ÈµL",
            "³X«È"
        };

        foreach (var marker in mojibakeMarkers)
        {
            Assert.DoesNotContain(marker, groupPlayer);
        }
    }
}
