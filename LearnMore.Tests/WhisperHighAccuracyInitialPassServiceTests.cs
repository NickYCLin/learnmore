using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public class WhisperHighAccuracyInitialPassServiceTests
{
    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldStopAutomaticRetryWhenYouTubeVideoUnavailable()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-unavailable",
                Title = "Biri-Biri",
                Artist = "YOASOBI",
                YouTubeUrl = "https://www.youtube.com/watch?v=shZyg5VFI1Y",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" }
                }
            }
        };
        var downloader = new FakeAudioDownloaderService
        {
            ExceptionToThrow = new Exception("yt-dlp failed: stderr=ERROR: [youtube] shZyg5VFI1Y: This video is not available")
        };
        var provider = new StubServiceProvider();
        var persistence = new FakeSongPersistenceService();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(new FakeWhisperAudioPreprocessService());
        provider.Add<VocalOnsetDetectionService>(new FakeVocalOnsetDetectionService());
        provider.Add<IWhisperTranslationSourceService>(new FakeTranslationSourceService());
        provider.Add<IWhisperSongPersistenceService>(persistence);
        provider.Add<IWhisperPostProcessService>(new FakeWhisperPostProcessService());
        var service = new WhisperHighAccuracyInitialPassService(
            new StubScopeFactory(provider),
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                InitialSegmentationHighAccuracyModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-unavailable");

        Assert.Equal("high_accuracy_failed", persistence.HighAccuracyStatuses.Last());
        Assert.Equal("YouTube 影片不可用，已停止自動補跑", persistence.HighAccuracyStatusReasons.Last());
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldSkipWhenModelIsNotConfigured()
    {
        var provider = new StubServiceProvider();
        var persistence = new FakeSongPersistenceService();
        provider.Add<IWhisperSongPersistenceService>(persistence);
        var scopeFactory = new StubScopeFactory(provider);
        var service = new WhisperHighAccuracyInitialPassService(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions()),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-123");

        Assert.Equal(1, scopeFactory.CreateScopeCallCount);
        Assert.Single(persistence.HighAccuracyStatuses);
        Assert.Null(persistence.HighAccuracyStatuses[0]);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldMarkFailedWhenHighAccuracySegmentsAreEmpty()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-empty",
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
        var vocal = new FakeVocalOnsetDetectionService();
        var translation = new FakeTranslationSourceService();
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
                InitialSegmentationHighAccuracyModel = "small",
                InitialSegmentationHighAccuracyFallbackModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-empty");

        Assert.Equal("high_accuracy_processing", persistence.HighAccuracyStatuses.First());
        Assert.Equal("high_accuracy_failed", persistence.HighAccuracyStatuses.Last());
        Assert.Equal("高精度模型未產出有效分句", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRebuildWhenLyricsAreEmptyEvenIfRemoteFallbackIsDisabled()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-rebuild-empty",
                Title = "Rebuild Song",
                Artist = "Test Artist",
                YouTubeUrl = "https://youtu.be/test-rebuild-empty",
                Lyrics = new List<LyricSegment>()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            InitialSegments = new List<LyricSegment>
            {
                new() { TimeStamp = 4.0, Japanese = "長い髪" },
                new() { TimeStamp = 8.0, Japanese = "You are the one" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 4.0, Japanese = "長い髪", Chinese = "長長的頭髮" },
                    new() { TimeStamp = 8.0, Japanese = "You are the one", Chinese = "你就是唯一" }
                },
                TranslationSourceKind.Fallback)
        };
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var options = Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
        {
            InitialSegmentationHighAccuracyModel = "small",
            UseRemoteHighAccuracyApi = true,
            RemoteHighAccuracyApiBaseUrl = "https://learnmore-api.example/",
            RemoteHighAccuracyApiToken = "test-token",
            RemoteHighAccuracyApiFallbackToLocal = false
        });
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IWhisperTranslationSourceService>(translation);
        provider.Add<IWhisperSongPersistenceService>(persistence);
        provider.Add<IWhisperPostProcessService>(postProcess);
        provider.Add(new RemoteHighAccuracyAlignmentClient(
            options,
            new DummyHttpClientFactory(),
            new DummyLogger<RemoteHighAccuracyAlignmentClient>()));
        var service = new WhisperHighAccuracyInitialPassService(
            new StubScopeFactory(provider),
            options,
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-rebuild-empty");

        Assert.Equal("https://youtu.be/test-rebuild-empty", downloader.LastYouTubeUrl);
        Assert.Equal(new[] { "small" }, vocal.ModelOverrides);
        Assert.Empty(persistence.LastExistingLyricIds!);
        Assert.Equal("長い髪", persistence.LastSegments![0].Japanese);
        Assert.Equal("You are the one", persistence.LastSegments[1].Japanese);
        Assert.Equal("song-rebuild-empty", postProcess.LastSongUid);
        Assert.DoesNotContain("high_accuracy_needs_review", persistence.HighAccuracyStatuses);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses.Last());
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldSurfaceSpecificTranscribeFailureReason()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-timeout",
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
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>(),
                "local_faster_whisper_timeout",
                "timeout=180s")
        };
        var subtitles = new FakeYouTubeSubtitleDownloadService();
        var translation = new FakeTranslationSourceService();
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IYouTubeSubtitleDownloadService>(subtitles);
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

        await service.RunHighAccuracyInitialPassAsync("song-timeout");

        Assert.Equal(new[] { "small", "tiny" }, vocal.ModelOverrides);
        Assert.Equal("high_accuracy_failed", persistence.HighAccuracyStatuses.Last());
        Assert.Equal("高精度語音辨識逾時（模型 tiny）", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRetryFastLocalFallbackWhenPrimaryModelTimesOut()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-fast-fallback",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test-fast-fallback",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" },
                    new() { LyricID = 12, TimeStamp = 2.0, Japanese = "舊歌詞2", Chinese = "翻譯2" }
                }
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0.5)
        };
        var vocal = new FakeVocalOnsetDetectionService();
        vocal.AttemptResults.Enqueue(new VocalOnsetDetectionService.InitialSegmentAttemptResult(
            new List<LyricSegment>(),
            "local_faster_whisper_timeout",
            "timeout=600s"));
        vocal.AttemptResults.Enqueue(new VocalOnsetDetectionService.InitialSegmentAttemptResult(
            new List<LyricSegment>
            {
                new() { TimeStamp = 4.0, Japanese = "補救句1" },
                new() { TimeStamp = 8.0, Japanese = "補救句2" }
            }));
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 4.5, Japanese = "補救句1", Chinese = "翻譯A" },
                    new() { TimeStamp = 8.5, Japanese = "補救句2", Chinese = "翻譯B" }
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

        await service.RunHighAccuracyInitialPassAsync("song-fast-fallback");

        Assert.Equal(new[] { "small", "tiny" }, vocal.ModelOverrides);
        Assert.Contains("改用快速本機模型補救中", persistence.HighAccuracyStatusReasons);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses.Last());
        Assert.Equal(new[] { 11, 12 }, persistence.LastExistingLyricIds);
        Assert.Equal("補救句1", persistence.LastSegments![0].Japanese);
        Assert.Equal("翻譯A", persistence.LastSegments[0].Chinese);
        Assert.Equal("song-fast-fallback", postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldKeepCompleteSyncedLyricsWhenFallbackCoverageIsTooLow()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-low-coverage",
                Title = "pained",
                Artist = "Vaundy",
                YouTubeUrl = "https://youtu.be/test-low-coverage",
                Lyrics = Enumerable.Range(1, 12)
                    .Select(index => new LyricSegment
                    {
                        LyricID = index,
                        TimeStamp = index * 5,
                        Japanese = $"正しい同期歌詞{index}",
                        Chinese = $"正確翻譯{index}"
                    })
                    .ToList()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 10, Japanese = "もうもうもう雑音だけ" },
                    new() { TimeStamp = 20, Japanese = "聞き取れない会話" },
                    new() { TimeStamp = 30, Japanese = "A-A-A-A-A" }
                })
        };
        var translation = new FakeTranslationSourceService();
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

        await service.RunHighAccuracyInitialPassAsync("song-low-coverage");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("秒數未通過同影片驗證", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(translation.LastStableSegments);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldKeepCompleteSyncedLyricsWhenCandidateLineCountDiffers()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-count-mismatch",
                Title = "桜流し",
                Artist = "宇多田ヒカル",
                YouTubeUrl = "https://youtu.be/test-count-mismatch",
                Lyrics = Enumerable.Range(1, 12)
                    .Select(index => new LyricSegment
                    {
                        LyricID = index,
                        TimeStamp = index * 5,
                        Japanese = $"正しい同期歌詞{index}",
                        Chinese = $"正確翻譯{index}",
                        JapaneseRuby = $"ただしいどうきかし{index}",
                        Roman = $"tadashii douki kashi {index}"
                    })
                    .ToList()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                Enumerable.Range(1, 8)
                    .Select(index => new LyricSegment
                    {
                        TimeStamp = index * 6,
                        Japanese = index <= 4
                            ? $"正しい同期歌詞{index}"
                            : $"ASR追加分句{index}"
                    })
                    .ToList())
        };
        var translation = new FakeTranslationSourceService
        {
            UseInputAsResult = true
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

        await service.RunHighAccuracyInitialPassAsync("song-count-mismatch");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("行數與完整同步歌詞不同", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(translation.LastStableSegments);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRejectCompressedEnglishCandidateLyrics()
    {
        static LyricSegment Existing(int index, string japanese) => new()
        {
            LyricID = index,
            TimeStamp = index * 3,
            Japanese = japanese,
            Chinese = $"翻譯{index}",
            JapaneseRuby = japanese,
            Roman = japanese
        };

        var existingLyrics = new List<LyricSegment>
        {
            Existing(1, "La-la-la-la, light is dawning"),
            Existing(2, "La-la-la-la, shining on me"),
            Existing(3, "I had to learn to fall"),
            Existing(4, "夢の向こうの夜明け"),
            Existing(5, "ひだまりのような温もり"),
            Existing(6, "もらうのに慣れてた"),
            Existing(7, "でも本能で感じるの"),
            Existing(8, "想像より広い世界"),
            Existing(9, "抱きしめたいと思った"),
            Existing(10, "君がくれた全て"),
            Existing(11, "自分の力で飛ぶことって so hard"),
            Existing(12, "Break the dawn, dawn 今を生きる自分")
        };
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-compressed-english",
                Title = "YOAKE",
                Artist = "NiziU",
                YouTubeUrl = "https://youtu.be/test-compressed-english",
                Lyrics = existingLyrics
            }
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                existingLyrics.Select((lyric, index) => new LyricSegment
                {
                    TimeStamp = lyric.TimeStamp + 0.1,
                    Japanese = index switch
                    {
                        0 => "La-la-la-la,lightisdawning",
                        1 => "La-la-la-la,shiningonme",
                        2 => "Ihadtolearntofall",
                        _ => lyric.Japanese
                    }
                }).ToList())
        };
        var provider = new StubServiceProvider();
        var persistence = new FakeSongPersistenceService();
        var translation = new FakeTranslationSourceService { UseInputAsResult = true };
        var postProcess = new FakeWhisperPostProcessService();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" });
        provider.Add<IWhisperAudioPreprocessService>(new FakeWhisperAudioPreprocessService { Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0) });
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IWhisperTranslationSourceService>(translation);
        provider.Add<IWhisperSongPersistenceService>(persistence);
        provider.Add<IWhisperPostProcessService>(postProcess);
        var service = new WhisperHighAccuracyInitialPassService(
            new StubScopeFactory(provider),
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                InitialSegmentationHighAccuracyModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-compressed-english");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("英文詞被壓縮成一串", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(translation.LastStableSegments);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRejectIncompleteAudioEvidenceForCompleteLyrics()
    {
        var lyrics = Enumerable.Range(1, 24)
            .Select(index => new LyricSegment
            {
                LyricID = index,
                TimeStamp = index * 5,
                Japanese = $"完整歌詞{index}",
                Chinese = $"完整翻譯{index}",
                JapaneseRuby = $"かんぜんかし{index}",
                Roman = $"kanzen kashi {index}"
            })
            .ToList();
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-low-evidence-offset",
                Title = "光",
                Artist = "宇多田ヒカル",
                YouTubeUrl = "https://youtu.be/test-low-evidence-offset",
                Lyrics = lyrics
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var alignments = lyrics
            .Select((lyric, index) => new VocalOnsetDetectionService.LyricTimingAlignment(
                lyric.Japanese,
                lyric.TimeStamp,
                lyric.TimeStamp + 12,
                lyric.TimeStamp + 13,
                0.95,
                index != 11,
                index,
                index))
            .ToList();
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                Enumerable.Range(1, 11)
                    .Select(index => new LyricSegment
                    {
                        TimeStamp = index * 6,
                        Japanese = $"低覆蓋候選{index}"
                    })
                    .ToList()),
            AlignmentResult = new VocalOnsetDetectionService.AlignmentAttemptResult(
                true,
                alignments,
                null,
                null,
                200,
                23,
                "line_alignment")
        };
        var translation = new FakeTranslationSourceService { UseInputAsResult = true };
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
        var service = new WhisperHighAccuracyInitialPassService(
            new StubScopeFactory(provider),
            Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                InitialSegmentationHighAccuracyModel = "small"
            }),
            new DummyLogger<WhisperHighAccuracyInitialPassService>());

        await service.RunHighAccuracyInitialPassAsync("song-low-evidence-offset");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("秒數未通過同影片驗證", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(translation.LastStableSegments);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldUseOfficialSubtitleTimingWhenLineCountsMatch()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-official-subtitle-timing",
                Title = "好きだから。",
                Artist = "ユイカ",
                YouTubeUrl = "https://youtu.be/test-official-subtitles",
                Lyrics = Enumerable.Range(1, 12)
                    .Select(index => new LyricSegment
                    {
                        LyricID = index,
                        TimeStamp = index * 10,
                        Japanese = $"公式日文{index}",
                        Chinese = $"舊翻譯{index}",
                        Roman = $"roman{index}"
                    })
                    .ToList()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 30, Japanese = "雜訊片段" }
                })
        };
        var subtitles = new FakeYouTubeSubtitleDownloadService
        {
            TranslationResponse = Enumerable.Range(1, 13)
                .Select(index => new LyricSegment
                {
                    TimeStamp = index == 1 ? 1.14 : index * 3,
                    Japanese = index <= 12 ? $"官方翻譯{index}" : "歌名片尾"
                })
                .ToList()
        };
        var translation = new FakeTranslationSourceService();
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IYouTubeSubtitleDownloadService>(subtitles);
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

        await service.RunHighAccuracyInitialPassAsync("song-official-subtitle-timing");

        Assert.Equal("https://youtu.be/test-official-subtitles", subtitles.LastTranslationUrl);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("YouTube 官方字幕時間軸", persistence.HighAccuracyStatusReasons.Last());
        var persistedSegments = persistence.LastSegments!;
        Assert.Equal(new[] { 1.14, 6.00, 9.00 }, persistedSegments.Take(3).Select(segment => segment.TimeStamp));
        Assert.Equal(new[] { "官方翻譯1", "官方翻譯2", "官方翻譯3" }, persistedSegments.Take(3).Select(segment => segment.Chinese));
        Assert.Equal(new[] { "公式日文1", "公式日文2", "公式日文3" }, persistedSegments.Take(3).Select(segment => segment.Japanese));
        Assert.Equal(12, persistedSegments.Count);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldReleaseQueueWhenCompleteSnapshotAlignmentIsCancelled()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-alignment-timeout",
                Title = "pained",
                Artist = "Vaundy",
                YouTubeUrl = "https://youtu.be/test-alignment-timeout",
                Lyrics = Enumerable.Range(1, 12)
                    .Select(index => new LyricSegment
                    {
                        LyricID = index,
                        TimeStamp = index * 5,
                        Japanese = $"正しい同期歌詞{index}",
                        Chinese = $"正確翻譯{index}"
                    })
                    .ToList()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 10, Japanese = "低覆蓋候選" }
                }),
            ThrowAlignmentCancellation = true
        };
        var translation = new FakeTranslationSourceService();
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

        await service.RunHighAccuracyInitialPassAsync("song-alignment-timeout");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("秒數未通過同影片驗證", persistence.HighAccuracyStatusReasons.Last());
        Assert.NotNull(vocal.LastAlignmentSeeds);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRejectCollapsedAsrLinesBeforeOverwritingSyncedLyrics()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-collapsed-asr",
                Title = "17さいのうた。",
                Artist = "ユイカ",
                YouTubeUrl = "https://youtu.be/test-collapsed-asr",
                Lyrics = Enumerable.Range(1, 20)
                    .Select(index => new LyricSegment
                    {
                        LyricID = index,
                        TimeStamp = index * 4,
                        Japanese = $"正しい同期歌詞{index}",
                        Chinese = $"正確翻譯{index}"
                    })
                    .ToList()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0)
        };
        var collapsedLine = string.Join("", Enumerable.Range(1, 20).Select(index => $"正しい同期歌詞{index}"));
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 10, Japanese = collapsedLine },
                    new() { TimeStamp = 90, Japanese = "背景未来の私今そこで" },
                    new() { TimeStamp = 140, Japanese = "どうもありがとね" }
                })
        };
        var translation = new FakeTranslationSourceService();
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

        await service.RunHighAccuracyInitialPassAsync("song-collapsed-asr");

        Assert.Equal("high_accuracy_needs_review", persistence.HighAccuracyStatuses.Last());
        Assert.Contains("秒數未通過同影片驗證", persistence.HighAccuracyStatusReasons.Last());
        Assert.Null(translation.LastStableSegments);
        Assert.Null(persistence.LastSegments);
        Assert.Null(postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldFallbackToYouTubeAutoSubtitlesWhenHighAccuracySegmentsAreEmpty()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-youtube-fallback",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test-fallback",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" },
                    new() { LyricID = 12, TimeStamp = 2.0, Japanese = "舊歌詞2", Chinese = "翻譯2" }
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
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>(),
                "local_faster_whisper_timeout",
                "timeout=180s")
        };
        var subtitles = new FakeYouTubeSubtitleDownloadService
        {
            Response = new List<LyricSegment>
            {
                new() { TimeStamp = 4.0, Japanese = "字幕句1" },
                new() { TimeStamp = 8.0, Japanese = "字幕句2" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 4.0, Japanese = "字幕句1", Chinese = "翻譯A" },
                    new() { TimeStamp = 8.0, Japanese = "字幕句2", Chinese = "翻譯B" }
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
        provider.Add<IYouTubeSubtitleDownloadService>(subtitles);
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

        await service.RunHighAccuracyInitialPassAsync("song-youtube-fallback");

        Assert.Equal("https://youtu.be/test-fallback", subtitles.LastUrl);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses.Last());
        Assert.Equal(new[] { 11, 12 }, persistence.LastExistingLyricIds);
        Assert.Equal("字幕句1", persistence.LastSegments![0].Japanese);
        Assert.Equal("翻譯A", persistence.LastSegments[0].Chinese);
        Assert.Equal("song-youtube-fallback", postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldFallbackToYouTubeAutoSubtitlesWhenSnapshotHasNoExistingLyrics()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-empty-placeholders",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test-empty-placeholders",
                Lyrics = new List<LyricSegment>()
            }
        };
        var downloader = new FakeAudioDownloaderService { DownloadedPath = "/tmp/high-accuracy.mp3" };
        var preprocess = new FakeWhisperAudioPreprocessService
        {
            Result = new WhisperAudioPreprocessResult("/tmp/high-accuracy.trimmed.mp3", 0.5)
        };
        var vocal = new FakeVocalOnsetDetectionService
        {
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>(),
                "local_faster_whisper_timeout",
                "timeout=180s")
        };
        var subtitles = new FakeYouTubeSubtitleDownloadService
        {
            Response = new List<LyricSegment>
            {
                new() { TimeStamp = 4.0, Japanese = "字幕句1" },
                new() { TimeStamp = 8.0, Japanese = "字幕句2" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 4.0, Japanese = "字幕句1", Chinese = "翻譯A" },
                    new() { TimeStamp = 8.0, Japanese = "字幕句2", Chinese = "翻譯B" }
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
        provider.Add<IYouTubeSubtitleDownloadService>(subtitles);
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

        await service.RunHighAccuracyInitialPassAsync("song-empty-placeholders");

        Assert.Equal("https://youtu.be/test-empty-placeholders", subtitles.LastUrl);
        Assert.Equal("high_accuracy_completed", persistence.HighAccuracyStatuses.Last());
        Assert.Empty(persistence.LastExistingLyricIds!);
        Assert.Equal("字幕句1", persistence.LastSegments![0].Japanese);
        Assert.Equal("song-empty-placeholders", postProcess.LastSongUid);
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldAlignYouTubeSubtitleFallbackSegmentsBeforePersisting()
    {
        var queryService = new FakeLyricsQueryService
        {
            Snapshot = new SongLyricsProcessingSnapshot
            {
                SongUid = "song-align-fallback",
                Title = "頑張りたいソング",
                Artist = "轟はじめ",
                YouTubeUrl = "https://youtu.be/test-align-fallback",
                Lyrics = new List<LyricSegment>
                {
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" },
                    new() { LyricID = 12, TimeStamp = 2.0, Japanese = "舊歌詞2", Chinese = "翻譯2" }
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
            AttemptResult = new VocalOnsetDetectionService.InitialSegmentAttemptResult(
                new List<LyricSegment>(),
                "local_faster_whisper_timeout",
                "timeout=180s"),
            AlignmentResult = new VocalOnsetDetectionService.AlignmentAttemptResult(
                true,
                new List<VocalOnsetDetectionService.LyricTimingAlignment>
                {
                    new("字幕句1", 4.0, 3.5, 4.4, 0.95, true, 0, 2),
                    new("字幕句2", 8.0, 7.25, 8.1, 0.96, true, 3, 5)
                },
                null,
                null,
                12,
                2)
        };
        var subtitles = new FakeYouTubeSubtitleDownloadService
        {
            Response = new List<LyricSegment>
            {
                new() { TimeStamp = 4.0, Japanese = "字幕句1" },
                new() { TimeStamp = 8.0, Japanese = "字幕句2" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            UseInputAsResult = true
        };
        var persistence = new FakeSongPersistenceService();
        var postProcess = new FakeWhisperPostProcessService();
        var provider = new StubServiceProvider();
        provider.Add<IWhisperLyricsQueryService>(queryService);
        provider.Add<YtDlpAudioDownloaderService>(downloader);
        provider.Add<IWhisperAudioPreprocessService>(preprocess);
        provider.Add<VocalOnsetDetectionService>(vocal);
        provider.Add<IYouTubeSubtitleDownloadService>(subtitles);
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

        await service.RunHighAccuracyInitialPassAsync("song-align-fallback");

        Assert.Equal("/tmp/high-accuracy.trimmed.mp3", vocal.LastAlignmentAudioFilePath);
        Assert.Null(vocal.LastSecondaryAlignmentAudioFilePath);
        Assert.Equal(new[] { "字幕句1", "字幕句2" }, vocal.LastAlignmentSeeds!.Select(seed => seed.Text));
        Assert.Equal(new[] { 3.5, 7.25 }, translation.LastStableSegments!.Select(segment => segment.TimeStamp));
        Assert.Equal(new[] { 3.5, 7.25 }, persistence.LastSegments!.Select(segment => segment.TimeStamp));
    }

    [Fact]
    public async Task RunHighAccuracyInitialPassAsync_ShouldRebuildSongWithHighAccuracySegmentsAndRerunRubyRoman()
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
                    new() { LyricID = 11, TimeStamp = 1.0, Japanese = "舊歌詞1", Chinese = "翻譯1" },
                    new() { LyricID = 12, TimeStamp = 2.0, Japanese = "舊歌詞2", Chinese = "翻譯2" }
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
                new() { TimeStamp = 1.0, Japanese = "新歌詞1" },
                new() { TimeStamp = 3.0, Japanese = "新歌詞2" }
            }
        };
        var translation = new FakeTranslationSourceService
        {
            Result = new TranslationSourceResolutionResult(
                new List<LyricSegment>
                {
                    new() { TimeStamp = 1.5, Japanese = "新歌詞1", Chinese = "翻譯A" },
                    new() { TimeStamp = 3.5, Japanese = "新歌詞2", Chinese = "翻譯B" }
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

        Assert.Equal("song-123", queryService.LastSongUid);
        Assert.Equal("https://youtu.be/test", downloader.LastYouTubeUrl);
        Assert.Equal("/tmp/high-accuracy.trimmed.mp3", vocal.LastAudioFilePath);
        Assert.Equal("small", vocal.LastModelOverride);
        Assert.False(vocal.LastAllowOpenAiFallback);
        Assert.Equal(new[] { 11, 12 }, persistence.LastExistingLyricIds);
        Assert.Equal(1.5, persistence.LastSegments![0].TimeStamp, 3);
        Assert.Equal("翻譯A", persistence.LastSegments[0].Chinese);
        Assert.Equal("song-123", postProcess.LastSongUid);
    }

    private sealed class FakeLyricsQueryService : IWhisperLyricsQueryService
    {
        public SongLyricsProcessingSnapshot? Snapshot { get; init; }
        public string? LastSongUid { get; private set; }

        public Task<EditLyricsViewModel?> GetEditLyricsViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default)
            => Task.FromResult<EditLyricsViewModel?>(null);

        public Task<SongLyricsProcessingSnapshot?> GetSongProcessingSnapshotAsync(string songUid, CancellationToken cancellationToken = default)
        {
            LastSongUid = songUid;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeAudioDownloaderService : YtDlpAudioDownloaderService
    {
        public FakeAudioDownloaderService() : base(Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()), new DummyLogger<YtDlpAudioDownloaderService>())
        {
        }

        public string DownloadedPath { get; init; } = string.Empty;
        public Exception? ExceptionToThrow { get; init; }
        public string? LastYouTubeUrl { get; private set; }

        public override Task<string> DownloadAudioAsync(string youTubeUrl, bool extractAudioAsMp3 = true)
        {
            LastYouTubeUrl = youTubeUrl;
            if (ExceptionToThrow != null)
                return Task.FromException<string>(ExceptionToThrow);

            return Task.FromResult(DownloadedPath);
        }
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
            : base(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new DummyHttpClientFactory(), Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions()), new JapaneseRubyGeneratorService(new FakeEnv()), new DummyLogger<VocalOnsetDetectionService>())
        {
        }

        public List<LyricSegment> InitialSegments { get; init; } = new();
        public VocalOnsetDetectionService.InitialSegmentAttemptResult? AttemptResult { get; init; }
        public Queue<VocalOnsetDetectionService.InitialSegmentAttemptResult> AttemptResults { get; } = new();
        public VocalOnsetDetectionService.AlignmentAttemptResult? AlignmentResult { get; init; }
        public string? LastAudioFilePath { get; private set; }
        public string? LastModelOverride { get; private set; }
        public List<string?> ModelOverrides { get; } = new();
        public bool LastAllowOpenAiFallback { get; private set; }
        public string? LastAlignmentAudioFilePath { get; private set; }
        public string? LastSecondaryAlignmentAudioFilePath { get; private set; }
        public IReadOnlyList<VocalOnsetDetectionService.LyricTimingSeed>? LastAlignmentSeeds { get; private set; }
        public bool ThrowAlignmentCancellation { get; init; }

        public override Task<List<LyricSegment>> TranscribeInitialSegmentsAsync(string audioFilePath, string? localModelOverride, bool allowOpenAiFallback, CancellationToken cancellationToken = default)
        {
            LastAudioFilePath = audioFilePath;
            LastModelOverride = localModelOverride;
            ModelOverrides.Add(localModelOverride);
            LastAllowOpenAiFallback = allowOpenAiFallback;
            return Task.FromResult(InitialSegments);
        }

        public override Task<VocalOnsetDetectionService.InitialSegmentAttemptResult> TranscribeInitialSegmentsWithDiagnosticsAsync(string audioFilePath, string? localModelOverride, bool allowOpenAiFallback, CancellationToken cancellationToken = default)
        {
            LastAudioFilePath = audioFilePath;
            LastModelOverride = localModelOverride;
            ModelOverrides.Add(localModelOverride);
            LastAllowOpenAiFallback = allowOpenAiFallback;
            if (AttemptResults.Count > 0)
                return Task.FromResult(AttemptResults.Dequeue());

            return Task.FromResult(AttemptResult ?? new VocalOnsetDetectionService.InitialSegmentAttemptResult(InitialSegments));
        }

        public override Task<VocalOnsetDetectionService.AlignmentAttemptResult> AlignLyricsToAudioAsync(string audioFilePath, IReadOnlyList<VocalOnsetDetectionService.LyricTimingSeed> lyricSeeds, string? secondaryAlignmentAudioFilePath, CancellationToken cancellationToken = default)
        {
            LastAlignmentAudioFilePath = audioFilePath;
            LastSecondaryAlignmentAudioFilePath = secondaryAlignmentAudioFilePath;
            LastAlignmentSeeds = lyricSeeds.ToList();
            if (ThrowAlignmentCancellation)
                throw new OperationCanceledException(cancellationToken);

            return Task.FromResult(AlignmentResult ?? new VocalOnsetDetectionService.AlignmentAttemptResult(false, new List<VocalOnsetDetectionService.LyricTimingAlignment>(), "alignment-not-configured"));
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

    private sealed class FakeTranslationSourceService : IWhisperTranslationSourceService
    {
        public TranslationSourceResolutionResult Result { get; init; } = new(new List<LyricSegment>(), TranslationSourceKind.Fallback);
        public bool UseInputAsResult { get; init; }
        public IReadOnlyList<LyricSegment>? LastStableSegments { get; private set; }

        public Task<List<LyricSegment>?> TryPreAlignAsync(string title, string artist, IReadOnlyList<LyricSegment> timestampSegments, CancellationToken cancellationToken = default, bool preferMarumaruLineCount = false)
            => Task.FromResult<List<LyricSegment>?>(null);

        public Task<TranslationSourceResolutionResult> ResolveFinalSegmentsAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, List<LyricSegment>? preAlignedSegments, CancellationToken cancellationToken = default)
        {
            LastStableSegments = stableSegmentsToInsert.ToList();
            if (!UseInputAsResult)
                return Task.FromResult(Result);

            var cloned = stableSegmentsToInsert
                .Select(segment => new LyricSegment
                {
                    TimeStamp = segment.TimeStamp,
                    Japanese = segment.Japanese,
                    Chinese = segment.Chinese,
                    JapaneseRuby = segment.JapaneseRuby,
                    Roman = segment.Roman,
                    LyricID = segment.LyricID
                })
                .ToList();
            return Task.FromResult(new TranslationSourceResolutionResult(cloned, TranslationSourceKind.Fallback));
        }
    }

    private sealed class FakeSongPersistenceService : IWhisperSongPersistenceService
    {
        public IReadOnlyList<int>? LastExistingLyricIds { get; private set; }
        public IReadOnlyList<LyricSegment>? LastSegments { get; private set; }
        public List<string?> HighAccuracyStatuses { get; } = new();
        public List<string?> HighAccuracyStatusReasons { get; } = new();

        public Task<string> AddSongToDatabaseAsync(TranscribeRequest request) => Task.FromResult(string.Empty);
        public Task CreateDynamicSongTableAsync(string songUid) => Task.CompletedTask;
        public Task InsertTranscriptionToDynamicTableAsync(string songUid, string transcriptionJson) => Task.CompletedTask;
        public Task<string> CreateSummonedSongAsync(SummonRequest request, IReadOnlyCollection<LyricEntry> lyrics, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task InsertManualSegmentsAsync(string songUid, IReadOnlyCollection<TranscriptionSegment> segments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SongPlaceholderCreationResult> CreateSongWithPlaceholdersAsync(TranscribeRequest request, IReadOnlyCollection<LyricSegment> segments, CancellationToken cancellationToken = default) => Task.FromResult(new SongPlaceholderCreationResult());
        public Task AppendProducerSongAsync(string userEmail, string songUid, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<int>> UpdateSongTranslationsAsync(string songUid, IReadOnlyList<LyricSegment> finalSegments, IReadOnlyList<int> existingLyricIds, CancellationToken cancellationToken = default)
        {
            LastSegments = finalSegments.ToList();
            LastExistingLyricIds = existingLyricIds.ToList();
            return Task.FromResult(existingLyricIds.ToList());
        }

        public Task UpdateHighAccuracyStatusAsync(string songUid, string? highAccuracyStatus, string? highAccuracyStatusReason = null, CancellationToken cancellationToken = default)
        {
            HighAccuracyStatuses.Add(highAccuracyStatus);
            HighAccuracyStatusReasons.Add(highAccuracyStatusReason);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWhisperPostProcessService : IWhisperPostProcessService
    {
        public string? LastSongUid { get; private set; }
        public Task RunRubyRomanEnrichmentAsync(string songUid, CancellationToken cancellationToken = default)
        {
            LastSongUid = songUid;
            return Task.CompletedTask;
        }

        public void EnqueueRubyRomanEnrichment(string songUid)
        {
            LastSongUid = songUid;
        }
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;
        public int CreateScopeCallCount { get; private set; }

        public StubScopeFactory(IServiceProvider provider)
        {
            _provider = provider;
        }

        public IServiceScope CreateScope()
        {
            CreateScopeCallCount++;
            return new StubScope(_provider);
        }
    }

    private sealed class StubScope : IServiceScope
    {
        public StubScope(IServiceProvider provider)
        {
            ServiceProvider = provider;
        }

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

    private sealed class DummyLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
