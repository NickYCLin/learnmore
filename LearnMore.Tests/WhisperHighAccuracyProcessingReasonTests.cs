using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public class WhisperHighAccuracyProcessingReasonTests
{
    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldUpdateProcessingReasonAcrossStages()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-123",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" }
                }
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0.5)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            InitialSegments = new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "新歌詞1" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 1.5, Japanese = "新歌詞1", Chinese = "翻譯A" }
                },
                TranslationSourceKind.Fallback)
        };
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IWhisperTranslationSourceService>(translation);
        provider.Add<IWhisperSongPersistenceService>(persistence);
        provider.Add<IWhisperPostProcessService>(postProcess);
        var scopeFactory = new StubScopeFactory(provider);
        var service = new WhisperHighAccuracyInitialPassService(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                InitialSegmentationHighAccuracyModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-123");

        Assert.Contains("準備高精度補跑", persistence.HighAccuracyStatusReasons);
        Assert.Contains("下載高精度音訊中", persistence.HighAccuracyStatusReasons);
        Assert.Contains("高精度語音辨識中", persistence.HighAccuracyStatusReasons);
        Assert.Contains("高精度翻譯整理中", persistence.HighAccuracyStatusReasons);
        Assert.Contains("高精度注音補寫中", persistence.HighAccuracyStatusReasons);
        Assert.Null(persistence.HighAccuracyStatusReasons[^1]);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses[^1]);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_WhenTranslationsRemainIncomplete_ShouldQueueCodexTranslation()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-123",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" }
                }
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0.5)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            InitialSegments = new List<LyricSegment>
            {
                new() { TimeStamp = 1.0, Japanese = "新歌詞1" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 1.5, Japanese = "新歌詞1", Chinese = "翻譯中..." }
                },
                TranslationSourceKind.Fallback)
        };
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IWhisperTranslationSourceService>(translation);
        provider.Add<IWhisperSongPersistenceService>(persistence);
        provider.Add<IWhisperPostProcessService>(postProcess);
        var scopeFactory = new StubScopeFactory(provider);
        var service = new WhisperHighAccuracyInitialPassService(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                InitialSegmentationHighAccuracyModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-123");

        Assert.Equal("translation_pending_codex", persistence.HighAccuracyStatuses[^1]);
        Assert.Contains("後台補件", persistence.HighAccuracyStatusReasons[^1]);
        Assert.DoesNotContain("high_accuracy_completed", persistence.HighAccuracyStatuses);
    }

    private sealed class FakeLyricsQueryService : IWhisperLyricsQueryService
    {
        public SongLyricsProcessingSnapshot? Snapshot { get; init; }
        public Task<EditLyricsViewModel?> GetEditLyricsViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default)
            => Task.FromResult<EditLyricsViewModel?>(null);
        public Task<SongLyricsProcessingSnapshot?> GetSongProcessingSnapshotAsync(string songUid, CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);
    }

    private sealed class FakeAudioDownloaderService : YtDlpAudioDownloaderService
    {
        public FakeAudioDownloaderService() : base(Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()), new DummyLogger<YtDlpAudioDownloaderService>()) { }
        public string DownloadedPath { get; init; } = string.Empty;
        public override Task<string> DownloadAudioAsync(string youTubeUrl, bool extractAudioAsMp3 = true)
            => Task.FromResult(DownloadedPath);
    }

    private sealed class FakeWhisperAudioPreprocessService : IWhisperAudioPreprocessService
    {
        public WhisperAudioPreprocessResult Result { get; init; } = new("", 0);
        public Task<WhisperAudioPreprocessResult> TrimLeadingSilenceAsync(string audioFilePath, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class FakeVocalOnsetDetectionService : VocalOnsetDetectionService
    {
        public FakeVocalOnsetDetectionService()
            : base(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new DummyHttpClientFactory(), Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions()), new JapaneseRubyGeneratorService(new FakeEnv()), new DummyLogger<VocalOnsetDetectionService>()) { }
        public List<LyricSegment> InitialSegments { get; init; } = new();
        public override Task<List<LyricSegment>> TranscribeInitialSegmentsAsync(string audioFilePath, string? localModelOverride, bool allowOpenAiFallback, CancellationToken cancellationToken = default)
            => Task.FromResult(InitialSegments);
        public override Task<VocalOnsetDetectionService.InitialSegmentAttemptResult> TranscribeInitialSegmentsWithDiagnosticsAsync(string audioFilePath, string? localModelOverride, bool allowOpenAiFallback, CancellationToken cancellationToken = default)
            => Task.FromResult(new VocalOnsetDetectionService.InitialSegmentAttemptResult(InitialSegments));
    }

    private sealed class FakeTranslationSourceService : IWhisperTranslationSourceService
    {
        public TranslationSourceResolutionResult Result { get; init; } = new(new List<LyricSegment>(), TranslationSourceKind.Fallback);
        public Task<List<LyricSegment>?> TryPreAlignAsync(string title, string artist, IReadOnlyList<LyricSegment> timestampSegments, CancellationToken cancellationToken = default, bool preferMarumaruLineCount = false)
            => Task.FromResult<List<LyricSegment>?>(null);
        public Task<TranslationSourceResolutionResult> ResolveFinalSegmentsAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, List<LyricSegment>? preAlignedSegments, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class FakeSongPersistenceService : IWhisperSongPersistenceService
    {
        public List<string?> HighAccuracyStatuses { get; } = new();
        public List<string?> HighAccuracyStatusReasons { get; } = new();
        public Task<string> AddSongToDatabaseAsync(TranscribeRequest request) => Task.FromResult(string.Empty);
        public Task CreateDynamicSongTableAsync(string songUid) => Task.CompletedTask;
        public Task InsertTranscriptionToDynamicTableAsync(string songUid, string transcriptionJson) => Task.CompletedTask;
        public Task<string> CreateSummonedSongAsync(SummonRequest request, IReadOnlyCollection<LyricEntry> lyrics, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task InsertManualSegmentsAsync(string songUid, IReadOnlyCollection<TranscriptionSegment> segments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SongPlaceholderCreationResult> CreateSongWithPlaceholdersAsync(TranscribeRequest request, IReadOnlyCollection<LyricSegment> segments, CancellationToken cancellationToken = default) => Task.FromResult(new SongPlaceholderCreationResult());
        public Task<List<int>> UpdateSongTranslationsAsync(string songUid, IReadOnlyList<LyricSegment> finalSegments, IReadOnlyList<int> existingLyricIds, CancellationToken cancellationToken = default) => Task.FromResult(existingLyricIds.ToList());
        public Task UpdateHighAccuracyStatusAsync(string songUid, string? highAccuracyStatus, string? highAccuracyStatusReason = null, CancellationToken cancellationToken = default)
        {
            HighAccuracyStatuses.Add(highAccuracyStatus);
            HighAccuracyStatusReasons.Add(highAccuracyStatusReason);
            return Task.CompletedTask;
        }
        public Task AppendProducerSongAsync(string userEmail, string songUid, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeWhisperPostProcessService : IWhisperPostProcessService
    {
        public Task RunRubyRomanEnrichmentAsync(string songUid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void EnqueueRubyRomanEnrichment(string songUid) { }
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;
        public StubScopeFactory(IServiceProvider provider) { _provider = provider; }
        public IServiceScope CreateScope() => new StubScope(_provider);
    }

    private sealed class StubScope : IServiceScope
    {
        public StubScope(IServiceProvider provider) { ServiceProvider = provider; }
        public IServiceProvider ServiceProvider { get; }
        public void Dispose() { }
    }

    private sealed class StubServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public void Add<T>(T service) where T : notnull => _services[typeof(T)] = service;
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class FakeEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "LearnMore.Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = "/tmp";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "/tmp";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class DummyLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
