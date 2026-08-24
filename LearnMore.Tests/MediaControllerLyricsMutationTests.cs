using System.Text.Json;
using System.Text;
using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public class MediaControllerLyricsMutationTests
{
    [Fact]
    public async Task EditLyrics_Post_ShouldSetSuccessTempDataAndReturnRedirectJson()
    {
        var lyricsMutationService = new FakeWhisperLyricsMutationService();
        var controller = CreateController(lyricsMutationService);
        var request = new MediaController.EditLyricsRequest
        {
            SongUid = "song-123",
            Lyrics = new List<LyricSegment>
            {
                new() { LyricID = 7, TimeStamp = 1.23, Japanese = "歌詞", Chinese = "歌詞", Roman = "kashi" }
            }
        };

        var result = await controller.EditLyrics(request);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal("歌詞已成功更新！", controller.TempData["SuccessMessage"]);
        Assert.Equal("song-123", lyricsMutationService.LastSongUid);
        Assert.Single(lyricsMutationService.LastLyrics ?? Array.Empty<LyricSegment>());
        var payload = JsonSerializer.Serialize(json.Value);
        Assert.Contains("\"success\":true", payload);
        Assert.Contains("/Media/Manage", payload);
    }

    [Fact]
    public async Task UpdateOrder_ShouldReturnBadRequestWhenMutationServiceReportsCountMismatch()
    {
        var lyricsMutationService = new FakeWhisperLyricsMutationService
        {
            UpdateOrderException = new InvalidOperationException("Data mismatch: number of lyrics and new order count do not match.")
        };
        var controller = CreateController(lyricsMutationService);

        var result = await controller.UpdateOrder(new MediaController.UpdateOrderRequest
        {
            SongUid = "song-123",
            NewOrder = new List<int> { 2, 1 }
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Data mismatch: number of lyrics and new order count do not match.", badRequest.Value);
    }

    [Fact]
    public async Task DeleteLyric_ShouldReturnFailureJsonWhenMutationServiceDeletesNothing()
    {
        var lyricsMutationService = new FakeWhisperLyricsMutationService
        {
            DeleteLyricResult = false
        };
        var controller = CreateController(lyricsMutationService);

        var result = await controller.DeleteLyric(new MediaController.DeleteLyricRequest
        {
            songUid = "song-123",
            lyricId = 99
        });

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(99, lyricsMutationService.LastDeletedLyricId);
        var payload = JsonSerializer.Serialize(json.Value);
        Assert.Contains("\"success\":false", payload);
        Assert.Contains("No records deleted. The LyricID may not exist.", payload);
    }

    [Fact]
    public async Task WhisperLyricsMutationService_ShouldRejectInvalidSongUidBeforeOpeningDatabaseConnection()
    {
        var service = new WhisperLyricsMutationService(new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateLyricsAsync("bad uid", new List<LyricSegment>()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOrderAsync("bad uid", new List<int> { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteLyricAsync("bad uid", 1));
    }

    private static MediaController CreateController(IWhisperLyricsMutationService lyricsMutationService)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<Microsoft.AspNetCore.Http.Features.ISessionFeature>(new TestSessionFeature());
        var controller = new MediaController(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new FakeWhisperLyricsQueryService(),
            lyricsMutationService,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new FakeWhisperHighAccuracyInitialPassService(),
            NullLogger<MediaController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider()),
            Url = new StubUrlHelper()
        };

        return controller;
    }

    private sealed class FakeWhisperLyricsQueryService : IWhisperLyricsQueryService
    {
        public Task<EditLyricsViewModel?> GetEditLyricsViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default)
            => Task.FromResult<EditLyricsViewModel?>(new EditLyricsViewModel { SongUid = songUid });

        public Task<SongLyricsProcessingSnapshot?> GetSongProcessingSnapshotAsync(string songUid, CancellationToken cancellationToken = default)
            => Task.FromResult<SongLyricsProcessingSnapshot?>(null);
    }

    private sealed class FakeWhisperLyricsMutationService : IWhisperLyricsMutationService
    {
        public string? LastSongUid { get; private set; }
        public IReadOnlyCollection<LyricSegment>? LastLyrics { get; private set; }
        public IReadOnlyList<int>? LastOrder { get; private set; }
        public int LastDeletedLyricId { get; private set; }
        public Exception? UpdateOrderException { get; init; }
        public bool DeleteLyricResult { get; init; } = true;

        public Task UpdateLyricsAsync(string songUid, IReadOnlyCollection<LyricSegment> lyrics, CancellationToken cancellationToken = default)
        {
            LastSongUid = songUid;
            LastLyrics = lyrics;
            return Task.CompletedTask;
        }

        public Task UpdateOrderAsync(string songUid, IReadOnlyList<int> newOrder, CancellationToken cancellationToken = default)
        {
            LastSongUid = songUid;
            LastOrder = newOrder;
            if (UpdateOrderException != null)
            {
                throw UpdateOrderException;
            }

            return Task.CompletedTask;
        }

        public Task<bool> DeleteLyricAsync(string songUid, int lyricId, CancellationToken cancellationToken = default)
        {
            LastSongUid = songUid;
            LastDeletedLyricId = lyricId;
            return Task.FromResult(DeleteLyricResult);
        }
    }

    private sealed class FakeWhisperHighAccuracyInitialPassService : IWhisperHighAccuracyInitialPassService
    {
        public Task RunHighAccuracyInitialPassAsync(string songUid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void EnqueueHighAccuracyInitialPass(string songUid) { }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestSessionFeature : Microsoft.AspNetCore.Http.Features.ISessionFeature
    {
        public ISession Session { get; set; } = new TestSession();
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public TestSession()
        {
            Set("Email", Encoding.UTF8.GetBytes("tester@example.com"));
        }

        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }

    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new(new DefaultHttpContext(), new Microsoft.AspNetCore.Routing.RouteData(), new ActionDescriptor());
        public string? Action(UrlActionContext actionContext) => "/Media/Manage";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => "/Media/Manage";
        public string? RouteUrl(UrlRouteContext routeContext) => "/Media/Manage";
    }
}
