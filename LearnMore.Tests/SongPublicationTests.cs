using System.Data.SqlClient;
using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class SongPublicationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData(" 翻譯中... ")]
    [InlineData("翻譯中…")]
    [InlineData("待翻譯")]
    [InlineData("翻譯待補")]
    public void CompleteChineseRequiresEveryLine(string? placeholder)
    {
        Assert.False(SongTranslationCompleteness.HasCompleteChineseTranslations([]));
        Assert.False(SongTranslationCompleteness.HasCompleteChineseTranslations(
            [Line("測試一"), Line(placeholder)]));
        Assert.True(SongTranslationCompleteness.HasCompleteChineseTranslations(
            [Line("測試一"), Line("測試二")]));
    }

    [SqlAuditFact]
    public async Task DraftsStayOutOfPublicEndpointsAndManagersCanSeeThem()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("LEARNMORE_SQL_AUDIT"));
        Assert.StartsWith("127.0.0.1,", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
        var database = "LearnMorePublicationAudit_" + Guid.NewGuid().ToString("N");
        var master = builder.ConnectionString;
        await Execute(master, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        try
        {
            var connection = builder.ConnectionString;
            await Execute(connection, """
                CREATE TABLE dbo.Songs (SongID int IDENTITY PRIMARY KEY, SongUid nvarchar(100) UNIQUE,
                    Title nvarchar(255), Artist nvarchar(255), Performer nvarchar(255), Translator nvarchar(255),
                    TranslationSource nvarchar(255), YouTubeVideoUrl nvarchar(255), ChannelThumbnailUrl nvarchar(255),
                    SongType nvarchar(50), AddedByUserId int, AddedDate datetime DEFAULT GETDATE(),
                    HighAccuracyStatus nvarchar(50), HighAccuracyStatusReason nvarchar(200));
                INSERT INTO dbo.Songs(SongUid, Title, Artist, YouTubeVideoUrl, HighAccuracyStatus)
                    VALUES(N'legacy', N'Fixture legacy', N'Fixture', N'https://example.invalid/legacy', N'translation_pending_codex');
                ALTER TABLE dbo.Songs ADD IsPublished bit NOT NULL DEFAULT (1);
                CREATE TABLE dbo.Users (Id int IDENTITY PRIMARY KEY, Email nvarchar(255), Producer nvarchar(max),
                    Collaboration nvarchar(max), Manager bit NOT NULL, IsDisabled bit NOT NULL DEFAULT 0, DeletedAt datetime2);
                INSERT INTO dbo.Users (Email, Manager) VALUES ('member@example.invalid', 0), ('admin@example.invalid', 1);
                """);
            await Execute(connection, """
                CREATE VIEW dbo.V_SongsData AS SELECT SongID, SongUid, Title, Artist, YouTubeVideoUrl, ChannelThumbnailUrl,
                    0 AS ViewCount, 0 AS ViewCount_WeeklyGrowth, 0 AS ViewCount_MonthlyGrowth FROM dbo.Songs;
                """);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DefaultConnection"] = connection }).Build();
            var persistence = new WhisperSongPersistenceService(config, null!, null!, null!,
                NullLogger<WhisperSongPersistenceService>.Instance);
            var upload = await persistence.CreateSongWithPlaceholdersAsync(
                new TranscribeRequest { Title = "Fixture draft", Artist = "Fixture", YouTubeUrl = "https://example.invalid/draft" },
                [Line(null)], "member@example.invalid");
            var second = await persistence.CreateSongWithPlaceholdersAsync(
                new TranscribeRequest { Title = "Fixture ready", Artist = "Fixture", YouTubeUrl = "https://example.invalid/ready" },
                [Line(null)], "member@example.invalid");
            await persistence.FinalizeUploadTranslationsAsync(upload.SongUid, [Line("翻譯中…")], upload.LyricIds,
                "high_accuracy_completed", "fixture");
            await persistence.FinalizeUploadTranslationsAsync(second.SongUid, [Line("測試一")], second.LyricIds,
                "high_accuracy_pending", "fixture");

            var context = new DefaultHttpContext();
            context.Session = new EmptySession();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("example.invalid");
            var controllerContext = new ControllerContext { HttpContext = context };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var home = new HomeController(NullLogger<HomeController>.Instance, config, cache) { ControllerContext = controllerContext };

            foreach (var type in new[] { "all", "new", "weekly", "monthly" })
            {
                var list = Assert.IsType<List<Songs>>(Assert.IsType<ViewResult>(await home.Index(type)).Model);
                Assert.DoesNotContain(list, song => song.SongUid == upload.SongUid);
                Assert.Contains(list, song => song.SongUid == second.SongUid);
                Assert.Contains(list, song => song.SongUid == "legacy");
            }
            var search = Assert.IsType<List<Songs>>(Assert.IsType<ViewResult>(await home.Search("Fixture")).Model);
            Assert.DoesNotContain(search, song => song.SongUid == upload.SongUid);
            Assert.Contains(search, song => song.SongUid == second.SongUid);

            var seo = new SeoController(config, NullLogger<SeoController>.Instance) { ControllerContext = controllerContext };
            var sitemap = Assert.IsType<ContentResult>(await seo.Sitemap()).Content;
            Assert.DoesNotContain(upload.SongUid, sitemap);
            Assert.Contains(second.SongUid, sitemap);
            Assert.Contains("legacy", sitemap);

            var lyrics = new LyricsController(config, null!) { ControllerContext = controllerContext };
            Assert.IsType<RedirectToActionResult>(lyrics.Index(upload.SongUid));
            var catalog = new MobileCatalogController(config);
            Assert.IsType<NotFoundResult>(await catalog.Lyrics(upload.SongUid));
            Assert.IsType<OkObjectResult>(await catalog.Lyrics(second.SongUid));

            var manage = await new WhisperManageQueryService(config).GetPagedManageViewModelAsync("admin@example.invalid", 1, 1);
            var managed = Assert.Single(manage.ProducerSongs);
            Assert.Equal(upload.SongUid, managed.SongUid);
            Assert.False(managed.IsPublished);
            Assert.Equal("translation_pending_codex", managed.HighAccuracyStatus);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    private static LyricSegment Line(string? chinese) => new() { Japanese = "テスト", Chinese = chinese ?? "" };

    private static async Task Execute(string connection, string sql)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var command = new SqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class EmptySession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => [];
        public bool TryGetValue(string key, out byte[]? value) { value = null; return false; }
        public void Set(string key, byte[] value) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
