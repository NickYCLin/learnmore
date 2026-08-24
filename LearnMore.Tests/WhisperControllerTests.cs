using LearnMore.Controllers;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace LearnMore.Tests;

public class WhisperControllerTests
{
    [Fact]
    public void WhisperController_ShouldNotContainRemovedDeadPrivateHelpers()
    {
        var controllerType = typeof(WhisperController);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        Assert.Null(controllerType.GetMethod("ConvertToRubyFormatAsync", flags));
        Assert.Null(controllerType.GetMethod("OptimizeRubyFormat", flags));
        Assert.Null(controllerType.GetMethod("GetRubyFormattedTextAsync", flags));
        Assert.Null(controllerType.GetMethod("TranslateToChineseAsync", flags));
        Assert.Null(controllerType.GetMethod("ProcessJapaneseTextAsync", flags));
        Assert.Null(controllerType.GetMethod("InsertTranscriptionToDynamicTableAsync", flags));
        Assert.Null(controllerType.GetMethod("ParseTranscriptionToSegmentsAsync", flags));
    }

    [Fact]
    public async Task TryGetYouTubeSubtitlesAsync_ShouldDelegateToSubtitleWorkflowService()
    {
        var subtitleWorkflow = new FakeYouTubeSubtitleDownloadService
        {
            Response = new List<LyricSegment>
            {
                new() { TimeStamp = 1.23, Japanese = "字幕測試" }
            }
        };

        var controller = CreateSignedInController(
            new FakeWhisperTranscribeWorkflowService(),
            subtitleWorkflow);

        var result = await controller.TryGetYouTubeSubtitlesAsync("https://youtu.be/subtitle-test");

        var ok = Assert.IsType<OkObjectResult>(result);
        var subtitles = Assert.IsAssignableFrom<IEnumerable<LyricSegment>>(ok.Value);
        var segment = Assert.Single(subtitles);
        Assert.Equal("https://youtu.be/subtitle-test", subtitleWorkflow.LastUrl);
        Assert.Equal("字幕測試", segment.Japanese);
    }

    [Fact]
    public async Task Transcribe_ShouldDelegateToWorkflowServiceAndReturnOkPayload()
    {
        var workflow = new FakeWhisperTranscribeWorkflowService
        {
            Response = "{\"segments\":[1]}"
        };
        var controller = CreateSignedInController(
            workflow,
            new FakeYouTubeSubtitleDownloadService());

        var result = await controller.Transcribe(new TranscribeRequest
        {
            YouTubeUrl = "https://youtu.be/test",
            Language = "ja"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(workflow.LastRequest);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("segments", json);
        Assert.Equal("https://youtu.be/test", workflow.LastRequest?.YouTubeUrl);
    }

    [Fact]
    public void YtDlpAudioDownloaderService_ShouldFallbackToMatchingExtractedFileWhenRequestedPathIsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"whisper-path-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var requestedPath = Path.Combine(tempDir, "sample.mp3");
            var actualPath = requestedPath + ".mp3";
            File.WriteAllText(actualPath, "ok");

            var service = new YtDlpAudioDownloaderService(
                Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()),
                new DummyLogger<YtDlpAudioDownloaderService>());
            var resolved = service.ResolveDownloadedAudioPath(requestedPath);

            Assert.Equal(actualPath, resolved);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void YtDlpAudioDownloaderService_ShouldUseDefaultYtDlpPathWhenOptionIsEmpty()
    {
        var service = new YtDlpAudioDownloaderService(
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions
            {
                YtDlpPath = string.Empty,
                FfmpegPath = @"C:\\Tools\\ffmpeg.exe"
            }),
            new DummyLogger<YtDlpAudioDownloaderService>());

        Assert.Equal("yt-dlp", service.GetYtDlpExecutablePath());
        Assert.Equal(@"C:\\Tools\\ffmpeg.exe", service.GetFfmpegExecutablePath());
    }

    [Fact]
    public void YtDlpAudioDownloaderService_ShouldUseLongDefaultDownloadTimeout()
    {
        var options = new WhisperRuntimeOptions();
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "YtDlpAudioDownloaderService.cs"));

        Assert.Equal(600, options.YtDlpDownloadTimeoutSeconds);
        Assert.Contains("YtDlpDownloadTimeoutSeconds", source);
        Assert.DoesNotContain("TimeSpan.FromSeconds(120)", source);
    }

    [Fact]
    public void YouTubeSubtitleDownloadService_ShouldUseConfiguredYtDlpTimeoutAndDrainOutput()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "YouTubeSubtitleDownloadService.cs"));

        Assert.Contains("YtDlpDownloadTimeoutSeconds", source);
        Assert.Contains("ReadToEndAsync", source);
        Assert.Contains("Math.Max(30", source);
        Assert.DoesNotContain("CancelAfter(TimeSpan.FromSeconds(30))", source);
    }

    [Fact]
    public async Task OpenAiWhisperClientService_ShouldPersistTranscriptionJsonAndReturnResponseBody()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"whisper-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputRoot);
        var audioPath = Path.Combine(outputRoot, "sample.wav");
        await File.WriteAllTextAsync(audioPath, "stub");

        try
        {
            var service = new OpenAiWhisperClientService(
                new StubHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"segments\":[]}")
                }),
                Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions
                {
                    MyApiKey = "my-api-from-options",
                    WhisperJsonPath = outputRoot
                }),
                new DummyLogger<OpenAiWhisperClientService>());

            var response = await service.TranscribeAudioAsync(audioPath, "ja");

            Assert.Equal("{\"segments\":[]}", response);
            var savedPath = Path.Combine(outputRoot, "WhisperJson", "sample.json");
            Assert.True(File.Exists(savedPath));
            Assert.Equal("{\"segments\":[]}", await File.ReadAllTextAsync(savedPath));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAiWhisperClientService_ShouldReturnEmptyTupleWhenProcessJapaneseResponseContentIsEmpty()
    {
        var service = new OpenAiWhisperClientService(
            new StubHttpClientFactory(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")
            }),
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions
            {
                MyApiKey = "my-api-from-options"
            }),
            new DummyLogger<OpenAiWhisperClientService>());

        var result = await service.ProcessJapaneseTextAsync("テスト");

        Assert.Equal(string.Empty, result.RubyText);
        Assert.Equal(string.Empty, result.ChineseText);
    }

    [Fact]
    public async Task OpenAiWhisperClientService_ShouldUseApiKeyWhenMyApiKeyIsMissing()
    {
        string? authorization = null;
        var service = new OpenAiWhisperClientService(
            new StubHttpClientFactory(request =>
            {
                authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"話語有多少\"}}]}")
                };
            }),
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions
            {
                ApiKey = "api-key-from-options",
                MyApiKey = string.Empty
            }),
            new DummyLogger<OpenAiWhisperClientService>());

        var result = await service.BatchTranslateToChineseAsync("言葉の数だけ");

        Assert.Equal("話語有多少", result);
        Assert.Equal("Bearer api-key-from-options", authorization);
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static WhisperController CreateSignedInController(
        IWhisperTranscribeWorkflowService transcribeWorkflow,
        IYouTubeSubtitleDownloadService subtitleDownloadService)
    {
        var session = new TestSession();
        session.SetString("Email", "test@example.com");
        var httpContext = new DefaultHttpContext
        {
            Session = session
        };

        return new WhisperController(transcribeWorkflow, subtitleDownloadService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new();

        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IEnumerable<string> Keys => _values.Keys;

        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value) => _values.TryGetValue(key, out value);
    }

    private sealed class FakeWhisperTranscribeWorkflowService : IWhisperTranscribeWorkflowService
    {
        public string Response { get; init; } = string.Empty;
        public TranscribeRequest? LastRequest { get; private set; }

        public Task<string> ExecuteAsync(TranscribeRequest request)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private sealed class FakeYouTubeSubtitleDownloadService : IYouTubeSubtitleDownloadService
    {
        public List<LyricSegment>? Response { get; init; }
        public List<LyricSegment>? TranslationResponse { get; init; }
        public string? LastUrl { get; private set; }
        public string? LastTranslationUrl { get; private set; }

        public Task<List<LyricSegment>?> TryDownloadSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        {
            LastUrl = youTubeUrl;
            return Task.FromResult(Response);
        }

        public Task<List<LyricSegment>?> TryDownloadTranslationSubtitlesAsync(string youTubeUrl, CancellationToken cancellationToken = default)
        {
            LastTranslationUrl = youTubeUrl;
            return Task.FromResult(TranslationResponse);
        }

        public Task<List<LyricSegment>?> TryDownloadAutoCaptionTimeAnchorsAsync(string youTubeUrl, CancellationToken cancellationToken = default)
            => Task.FromResult<List<LyricSegment>?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(_handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class DummyLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
