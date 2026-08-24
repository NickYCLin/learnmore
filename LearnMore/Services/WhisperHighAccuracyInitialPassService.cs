using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LearnMore.Services;

public class WhisperHighAccuracyInitialPassService : IWhisperHighAccuracyInitialPassService
{
    private static readonly string HighAccuracyTracePath = @"D:\Data\learnmore-high-accuracy-trace.log";
    private static readonly SemaphoreSlim HighAccuracyQueueGate = new(1, 1);
    private static readonly ConcurrentQueue<string> HighAccuracyQueue = new();
    private static readonly ConcurrentDictionary<string, byte> QueuedOrRunningSongUids = new(StringComparer.Ordinal);
    private static int QueueWorkerStarted;
    private const string PendingReason = "準備高精度補跑";
    private const string DownloadingReason = "下載高精度音訊中";
    private const string TranscribingReason = "高精度語音辨識中";
    private const string TranslatingReason = "高精度翻譯整理中";
    private const string PostProcessingReason = "高精度注音補寫中";
    private const string RemoteApiAligningReason = "呼叫遠端高精度校準中";
    private const string FastLocalModelFallbackReason = "改用快速本機模型補救中";
    private const string YouTubeSubtitleFallbackReason = "改用 YouTube 自動字幕補救中";
    private const string MissingSnapshotDataReason = "缺少歌曲快照或歌詞資料";
    private const string DownloadEmptyReason = "高精度音訊下載失敗";
    private const string PermanentYouTubeUnavailableReason = "YouTube 影片不可用，已停止自動補跑";
    private const string NoSegmentsReason = "高精度模型未產出有效分句";
    private const string LowCoverageReason = "高精度模型結果與原歌詞覆蓋不足";
    private const string InvalidTimingReason = "高精度模型結果秒數超出音訊長度";
    private const string UnhandledExceptionReason = "背景高精度補跑發生未處理例外";
    private static readonly TimeSpan CompleteSnapshotAlignmentTimeout = TimeSpan.FromMinutes(12);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VocalOnsetDetectionOptions _options;
    private readonly ILogger<WhisperHighAccuracyInitialPassService> _logger;

    public WhisperHighAccuracyInitialPassService(
        IServiceScopeFactory scopeFactory,
        IOptions<VocalOnsetDetectionOptions> options,
        ILogger<WhisperHighAccuracyInitialPassService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunHighAccuracyInitialPassAsync(string songUid, CancellationToken cancellationToken = default)
    {
        AppendTrace($"start songUid={songUid}; model={_options.InitialSegmentationHighAccuracyModel}");
        if (!IsRemoteHighAccuracyApiConfiguredFromOptions() && string.IsNullOrWhiteSpace(_options.InitialSegmentationHighAccuracyModel))
        {
            _logger.LogInformation("高精度初始分句未設定模型，跳過 songUid={SongUid}", songUid);
            AppendTrace($"skip:no-model songUid={songUid}");
            await SetHighAccuracyStatusSafeAsync(songUid, null, null, cancellationToken);
            return;
        }

        await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", PendingReason, cancellationToken);

        using var scope = _scopeFactory.CreateScope();
        var songPersistence = scope.ServiceProvider.GetRequiredService<IWhisperSongPersistenceService>();
        var remoteAlignmentClient = scope.ServiceProvider.GetService<RemoteHighAccuracyAlignmentClient>();
        if (!IsRemoteHighAccuracyApiConfigured(remoteAlignmentClient) && string.IsNullOrWhiteSpace(_options.InitialSegmentationHighAccuracyModel))
        {
            _logger.LogInformation("高精度初始分句未設定模型，跳過 songUid={SongUid}", songUid);
            AppendTrace($"skip:no-model songUid={songUid}");
            await SetHighAccuracyStatusSafeAsync(songUid, null, null, cancellationToken);
            return;
        }

        var queryService = scope.ServiceProvider.GetRequiredService<IWhisperLyricsQueryService>();
        var downloader = scope.ServiceProvider.GetRequiredService<YtDlpAudioDownloaderService>();
        var preprocessService = scope.ServiceProvider.GetRequiredService<IWhisperAudioPreprocessService>();
        var vocalOnsetService = scope.ServiceProvider.GetRequiredService<VocalOnsetDetectionService>();
        var translationSourceService = scope.ServiceProvider.GetRequiredService<IWhisperTranslationSourceService>();
        var postProcessService = scope.ServiceProvider.GetRequiredService<IWhisperPostProcessService>();
        var youTubeSubtitleDownloadService = scope.ServiceProvider.GetService<IYouTubeSubtitleDownloadService>();

        var snapshot = await queryService.GetSongProcessingSnapshotAsync(songUid, cancellationToken);
        AppendTrace($"snapshot songUid={songUid}; found={(snapshot != null)}; lyricCount={(snapshot?.Lyrics.Count ?? 0)}; url={snapshot?.YouTubeUrl}");
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.YouTubeUrl))
        {
            _logger.LogInformation("高精度初始分句缺少必要資料，跳過 songUid={SongUid}", songUid);
            AppendTrace($"skip:missing-snapshot-data songUid={songUid}");
            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_failed", MissingSnapshotDataReason, cancellationToken);
            return;
        }

        if (await TryRunRemoteHighAccuracyAlignmentAsync(songUid, snapshot, songPersistence, remoteAlignmentClient, cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.InitialSegmentationHighAccuracyModel))
        {
            _logger.LogInformation("高精度初始分句未設定本機模型，無法改用本機補跑 songUid={SongUid}", songUid);
            AppendTrace($"skip:no-local-model-after-remote songUid={songUid}");
            await SetHighAccuracyStatusSafeAsync(
                songUid,
                "high_accuracy_failed",
                "遠端高精度校準未完成，且未設定本機模型補跑。",
                cancellationToken);
            return;
        }

        string? audioFilePath = null;
        string? processedAudioFilePath = null;
        try
        {
            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", DownloadingReason, cancellationToken);
            try
            {
                audioFilePath = await downloader.DownloadAudioAsync(snapshot.YouTubeUrl, extractAudioAsMp3: true);
            }
            catch (Exception ex) when (IsPermanentYouTubeDownloadFailure(ex))
            {
                AppendTrace($"download-permanent-failure songUid={songUid}; reason=video_unavailable; error={ex.Message}");
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_failed", PermanentYouTubeUnavailableReason, cancellationToken);
                return;
            }

            AppendTrace($"downloaded songUid={songUid}; audio={audioFilePath}");
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                _logger.LogWarning("高精度初始分句下載音訊失敗 songUid={SongUid}", songUid);
                AppendTrace($"skip:download-empty songUid={songUid}");
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_failed", DownloadEmptyReason, cancellationToken);
                return;
            }

            var preprocessResult = await preprocessService.TrimLeadingSilenceAsync(audioFilePath, cancellationToken);
            processedAudioFilePath = preprocessResult.AudioFilePath;
            AppendTrace($"preprocess songUid={songUid}; audio={processedAudioFilePath}; trim={preprocessResult.TrimOffsetSeconds:F3}");
            var audioDurationSeconds = await TryGetAudioDurationSecondsAsync(
                processedAudioFilePath,
                cancellationToken);
            AppendTrace($"audio-duration songUid={songUid}; duration={(audioDurationSeconds?.ToString("F2") ?? "<null>")}");

            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", TranscribingReason, cancellationToken);
            var highAccuracyAttempt = await vocalOnsetService.TranscribeInitialSegmentsWithDiagnosticsAsync(
                processedAudioFilePath,
                _options.InitialSegmentationHighAccuracyModel,
                allowOpenAiFallback: false,
                cancellationToken);
            List<LyricSegment> highAccuracySegments = highAccuracyAttempt.Segments;
            var failedModel = _options.InitialSegmentationHighAccuracyModel;
            var usedYouTubeSubtitleFallback = false;
            AppendTrace($"transcribe songUid={songUid}; segmentCount={highAccuracySegments.Count}; reason={(highAccuracyAttempt.FailureReason ?? "<null>")}; detail={(highAccuracyAttempt.FailureDetail ?? "<null>")}");

            var fallbackModel = ResolveFastLocalFallbackModel();
            if (highAccuracySegments.Count == 0 && ShouldRetryWithFastLocalModel(highAccuracyAttempt.FailureReason, fallbackModel))
            {
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", FastLocalModelFallbackReason, cancellationToken);
                var fallbackAttempt = await vocalOnsetService.TranscribeInitialSegmentsWithDiagnosticsAsync(
                    processedAudioFilePath,
                    fallbackModel,
                    allowOpenAiFallback: false,
                    cancellationToken);
                AppendTrace($"fallback:fast-local songUid={songUid}; model={fallbackModel}; segmentCount={fallbackAttempt.Segments.Count}; reason={(fallbackAttempt.FailureReason ?? "<null>")}; detail={(fallbackAttempt.FailureDetail ?? "<null>")}");
                if (fallbackAttempt.Segments.Count > 0)
                {
                    highAccuracyAttempt = fallbackAttempt;
                    highAccuracySegments = fallbackAttempt.Segments;
                    failedModel = fallbackModel;
                }
                else if (!string.IsNullOrWhiteSpace(fallbackAttempt.FailureReason))
                {
                    highAccuracyAttempt = fallbackAttempt;
                    failedModel = fallbackModel;
                }
            }

            if (preprocessResult.TrimOffsetSeconds > 0 && highAccuracySegments.Count > 0)
            {
                foreach (var segment in highAccuracySegments)
                    segment.TimeStamp += preprocessResult.TrimOffsetSeconds;
            }

            if (highAccuracySegments.Count == 0 && youTubeSubtitleDownloadService != null)
            {
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", YouTubeSubtitleFallbackReason, cancellationToken);
                var subtitleSegments = await youTubeSubtitleDownloadService.TryDownloadSubtitlesAsync(snapshot.YouTubeUrl, cancellationToken);
                if (subtitleSegments != null && subtitleSegments.Count > 0)
                {
                    highAccuracySegments = subtitleSegments;
                    usedYouTubeSubtitleFallback = true;
                    AppendTrace($"fallback:youtube-subtitles songUid={songUid}; segmentCount={highAccuracySegments.Count}");
                }
                else
                {
                    AppendTrace($"fallback:youtube-subtitles-empty songUid={songUid}");
                }
            }

            if (highAccuracySegments.Count == 0)
            {
                _logger.LogWarning("高精度初始分句未產出可用結果 songUid={SongUid}, reason={Reason}, detail={Detail}", songUid, highAccuracyAttempt.FailureReason, highAccuracyAttempt.FailureDetail);
                AppendTrace($"skip:no-segments songUid={songUid}; reason={(highAccuracyAttempt.FailureReason ?? "<null>")}; detail={(highAccuracyAttempt.FailureDetail ?? "<null>")}");
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_failed", ResolveHighAccuracyFailureReason(highAccuracyAttempt.FailureReason, failedModel), cancellationToken);
                return;
            }

            if (!AreTimestampsWithinAudioDuration(highAccuracySegments, audioDurationSeconds, out var timingIssue))
            {
                AppendTrace($"reject:invalid-timing songUid={songUid}; {timingIssue}");
                if (HasCompleteSnapshotLyrics(snapshot.Lyrics))
                {
                    await SetHighAccuracyStatusSafeAsync(
                        songUid,
                        "high_accuracy_needs_review",
                        $"{InvalidTimingReason}：{timingIssue}；已保留既有歌詞，避免覆蓋成錯誤時間軸。",
                        cancellationToken);
                    return;
                }

                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "high_accuracy_failed",
                    $"{InvalidTimingReason}：{timingIssue}",
                    cancellationToken);
                return;
            }

            if (usedYouTubeSubtitleFallback)
            {
                var lyricSeeds = highAccuracySegments
                    .Select(segment => new VocalOnsetDetectionService.LyricTimingSeed(segment.Japanese ?? string.Empty, segment.TimeStamp))
                    .ToList();
                var alignmentAttempt = await vocalOnsetService.AlignLyricsToAudioAsync(
                    processedAudioFilePath,
                    lyricSeeds,
                    cancellationToken);
                AppendTrace($"fallback-alignment songUid={songUid}; success={alignmentAttempt.IsSuccess}; alignments={alignmentAttempt.Alignments.Count}; matched={alignmentAttempt.MatchedCount}; reason={(alignmentAttempt.FailureReason ?? "<null>")}");
                if (alignmentAttempt.IsSuccess && alignmentAttempt.Alignments.Count == highAccuracySegments.Count)
                {
                    for (var i = 0; i < highAccuracySegments.Count; i++)
                    {
                        var alignment = alignmentAttempt.Alignments[i];
                        if (!alignment.IsMatched)
                            continue;

                        highAccuracySegments[i].TimeStamp = alignment.Start;
                    }
                }
            }

            if (!HasAdequateLyricCoverage(highAccuracySegments, snapshot.Lyrics))
            {
                AppendTrace($"reject:low-lyric-coverage songUid={songUid}; candidate={highAccuracySegments.Count}; existing={snapshot.Lyrics.Count}");
                if (HasCompleteSnapshotLyrics(snapshot.Lyrics))
                {
                    if (await TryRecoverCompleteSnapshotTimingAsync(
                        songUid,
                        snapshot,
                        processedAudioFilePath,
                        vocalOnsetService,
                        youTubeSubtitleDownloadService,
                        songPersistence,
                        cancellationToken))
                    {
                        return;
                    }

                    await SetHighAccuracyStatusSafeAsync(
                        songUid,
                        "high_accuracy_needs_review",
                        "同影片高精度補跑與原歌詞覆蓋不足；已保留完整同步歌詞，但秒數未通過同影片驗證。",
                        cancellationToken);
                    return;
                }

                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_failed", LowCoverageReason, cancellationToken);
                return;
            }

            if (HasCompleteSnapshotLyrics(snapshot.Lyrics) && highAccuracySegments.Count != snapshot.Lyrics.Count)
            {
                AppendTrace($"reject:complete-snapshot-count-mismatch songUid={songUid}; candidate={highAccuracySegments.Count}; existing={snapshot.Lyrics.Count}");
                if (await TryRecoverCompleteSnapshotTimingAsync(
                    songUid,
                    snapshot,
                    processedAudioFilePath,
                    vocalOnsetService,
                    youTubeSubtitleDownloadService,
                    songPersistence,
                    cancellationToken))
                {
                    return;
                }

                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "high_accuracy_needs_review",
                    "同影片高精度補跑行數與完整同步歌詞不同；已保留完整歌詞，等待更可靠的逐句秒數校正。",
                    cancellationToken);
                return;
            }

            if (TryGetCandidateLyricsQualityIssue(highAccuracySegments, snapshot.Lyrics, out var qualityIssueReason))
            {
                AppendTrace($"reject:candidate-quality songUid={songUid}; reason={qualityIssueReason}; candidate={highAccuracySegments.Count}; existing={snapshot.Lyrics.Count}");
                if (HasCompleteSnapshotLyrics(snapshot.Lyrics))
                {
                    await SetHighAccuracyStatusSafeAsync(
                        songUid,
                        "high_accuracy_needs_review",
                        $"高精度模型結果品質不足：{qualityIssueReason}；已保留既有歌詞，等待人工檢查。",
                        cancellationToken);
                    return;
                }

                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "high_accuracy_failed",
                    $"高精度模型結果品質不足：{qualityIssueReason}",
                    cancellationToken);
                return;
            }

            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", TranslatingReason, cancellationToken);
            var translationResolution = await translationSourceService.ResolveFinalSegmentsAsync(
                snapshot.Title,
                snapshot.Artist,
                highAccuracySegments,
                preAlignedSegments: null,
                cancellationToken);
            AppendTrace($"translation songUid={songUid}; source={translationResolution.Source}; count={translationResolution.Segments.Count}");

            await songPersistence.UpdateSongTranslationsAsync(
                songUid,
                translationResolution.Segments,
                snapshot.Lyrics.Select(lyric => lyric.LyricID).ToList(),
                cancellationToken);
            AppendTrace($"updated songUid={songUid}");

            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", PostProcessingReason, cancellationToken);
            await postProcessService.RunRubyRomanEnrichmentAsync(songUid, cancellationToken);
            AppendTrace($"postprocess songUid={songUid}");
            if (!HasCompleteChineseTranslations(translationResolution.Segments))
            {
                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "translation_pending_codex",
                    "翻譯未完成：已排入後台補件，完成驗證後會自動標記高精度完成。",
                    cancellationToken);
                return;
            }

            await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_completed", null, cancellationToken);
        }
        finally
        {
            CleanupFile(processedAudioFilePath);
            if (!string.Equals(audioFilePath, processedAudioFilePath, StringComparison.OrdinalIgnoreCase))
                CleanupFile(audioFilePath);
            AppendTrace($"cleanup songUid={songUid}; primary={audioFilePath}; processed={processedAudioFilePath}");
        }
    }

    private async Task<bool> TryRunRemoteHighAccuracyAlignmentAsync(
        string songUid,
        SongLyricsProcessingSnapshot snapshot,
        IWhisperSongPersistenceService songPersistence,
        RemoteHighAccuracyAlignmentClient? remoteAlignmentClient,
        CancellationToken cancellationToken)
    {
        if (remoteAlignmentClient == null || !remoteAlignmentClient.IsConfigured)
        {
            return false;
        }

        if (snapshot.Lyrics.Count == 0)
        {
            AppendTrace($"remote-api-alignment-skipped-empty-lyrics songUid={songUid}");
            return false;
        }

        await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_processing", RemoteApiAligningReason, cancellationToken);
        try
        {
            var alignmentAttempt = await remoteAlignmentClient.AlignAsync(snapshot, cancellationToken);
            AppendTrace(
                $"remote-api-alignment songUid={songUid}; success={alignmentAttempt.IsSuccess}; alignments={alignmentAttempt.Alignments.Count}; matched={alignmentAttempt.MatchedCount}; strategy={(alignmentAttempt.CorrectionStrategy ?? "<null>")}; reason={(alignmentAttempt.FailureReason ?? "<null>")}; detail={(alignmentAttempt.FailureDetail ?? "<null>")}");

            if (alignmentAttempt.IsSuccess
                && alignmentAttempt.Alignments.Count == snapshot.Lyrics.Count
                && TryBuildAlignedCompleteSnapshot(
                    snapshot.Lyrics,
                    alignmentAttempt.Alignments,
                    out var alignedSegments,
                    out var matchedCount))
            {
                await songPersistence.UpdateSongTranslationsAsync(
                    songUid,
                    alignedSegments,
                    snapshot.Lyrics.Select(lyric => lyric.LyricID).ToList(),
                    cancellationToken);

                var reason = $"同影片音訊已完成逐句時間校正，共 {alignedSegments.Count} 行，命中 {matchedCount} 行。";
                await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_completed", reason, cancellationToken);
                return true;
            }

            AppendTrace($"remote-api-alignment-rejected songUid={songUid}; lyricCount={snapshot.Lyrics.Count}; alignmentCount={alignmentAttempt.Alignments.Count}");
            if (!_options.RemoteHighAccuracyApiFallbackToLocal)
            {
                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "high_accuracy_needs_review",
                    "遠端高精度校準結果未通過完整歌詞驗證，已保留既有歌詞。",
                    cancellationToken);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendTrace($"remote-api-alignment-failed songUid={songUid}; error={ex.Message}");
            _logger.LogWarning(ex, "LearnMoreAPI 高精度校準失敗 songUid={SongUid}", songUid);
            if (!_options.RemoteHighAccuracyApiFallbackToLocal)
            {
                await SetHighAccuracyStatusSafeAsync(
                    songUid,
                    "high_accuracy_failed",
                    "遠端高精度校準失敗，且已設定不回退本機補跑。",
                    cancellationToken);
                return true;
            }
        }

        return false;
    }

    private bool IsRemoteHighAccuracyApiConfigured(RemoteHighAccuracyAlignmentClient? remoteAlignmentClient)
        => remoteAlignmentClient?.IsConfigured == true
            || IsRemoteHighAccuracyApiConfiguredFromOptions();

    private bool IsRemoteHighAccuracyApiConfiguredFromOptions()
        => _options.UseRemoteHighAccuracyApi
            && !string.IsNullOrWhiteSpace(_options.RemoteHighAccuracyApiBaseUrl)
            && !string.IsNullOrWhiteSpace(_options.RemoteHighAccuracyApiToken);

    private async Task<bool> TryRecoverCompleteSnapshotTimingAsync(
        string songUid,
        SongLyricsProcessingSnapshot snapshot,
        string processedAudioFilePath,
        VocalOnsetDetectionService vocalOnsetService,
        IYouTubeSubtitleDownloadService? youTubeSubtitleDownloadService,
        IWhisperSongPersistenceService songPersistence,
        CancellationToken cancellationToken)
    {
        if (youTubeSubtitleDownloadService != null
            && await TryApplyOfficialSubtitleTimingAsync(
                songUid,
                snapshot,
                youTubeSubtitleDownloadService,
                songPersistence,
                cancellationToken))
        {
            return true;
        }

        return await TryAlignCompleteSnapshotLyricsAsync(
            songUid,
            snapshot,
            processedAudioFilePath,
            vocalOnsetService,
            songPersistence,
            cancellationToken);
    }

    private async Task<bool> TryAlignCompleteSnapshotLyricsAsync(
        string songUid,
        SongLyricsProcessingSnapshot snapshot,
        string processedAudioFilePath,
        VocalOnsetDetectionService vocalOnsetService,
        IWhisperSongPersistenceService songPersistence,
        CancellationToken cancellationToken)
    {
        var lyricSeeds = snapshot.Lyrics
            .Select(lyric => new VocalOnsetDetectionService.LyricTimingSeed(lyric.Japanese ?? string.Empty, lyric.TimeStamp))
            .ToList();

        VocalOnsetDetectionService.AlignmentAttemptResult alignmentAttempt;
        using (var alignmentTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            alignmentTimeoutCts.CancelAfter(CompleteSnapshotAlignmentTimeout);
            try
            {
                alignmentAttempt = await vocalOnsetService.AlignLyricsToAudioAsync(
                    processedAudioFilePath,
                    lyricSeeds,
                    alignmentTimeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppendTrace($"complete-snapshot-alignment-timeout songUid={songUid}; timeoutSeconds={CompleteSnapshotAlignmentTimeout.TotalSeconds:0}");
                return false;
            }
        }

        AppendTrace(
            $"complete-snapshot-alignment songUid={songUid}; success={alignmentAttempt.IsSuccess}; alignments={alignmentAttempt.Alignments.Count}; matched={alignmentAttempt.MatchedCount}; strategy={(alignmentAttempt.CorrectionStrategy ?? "<null>")}; offset={(alignmentAttempt.FixedOffsetSeconds?.ToString("F2") ?? "<null>")}; reason={(alignmentAttempt.FailureReason ?? "<null>")}");

        if (!alignmentAttempt.IsSuccess || alignmentAttempt.Alignments.Count != snapshot.Lyrics.Count)
        {
            return false;
        }

        if (!TryBuildAlignedCompleteSnapshot(
            snapshot.Lyrics,
            alignmentAttempt.Alignments,
            out var alignedSegments,
            out var matchedCount))
        {
            AppendTrace($"complete-snapshot-alignment-rejected songUid={songUid}; matched={matchedCount}; total={snapshot.Lyrics.Count}");
            return false;
        }

        await songPersistence.UpdateSongTranslationsAsync(
            songUid,
            alignedSegments,
            snapshot.Lyrics.Select(lyric => lyric.LyricID).ToList(),
            cancellationToken);

        var reason = $"完整同步歌詞經同影片 ASR 驗證，已完成逐句時間校正，共 {alignedSegments.Count} 行，命中 {matchedCount} 行。";
        await SetHighAccuracyStatusSafeAsync(songUid, "high_accuracy_completed", reason, cancellationToken);
        return true;
    }

    private async Task<bool> TryApplyOfficialSubtitleTimingAsync(
        string songUid,
        SongLyricsProcessingSnapshot snapshot,
        IYouTubeSubtitleDownloadService subtitleDownloadService,
        IWhisperSongPersistenceService songPersistence,
        CancellationToken cancellationToken)
    {
        var subtitles = await subtitleDownloadService.TryDownloadTranslationSubtitlesAsync(snapshot.YouTubeUrl, cancellationToken);
        if (subtitles == null || subtitles.Count == 0)
        {
            AppendTrace($"official-subtitle-timing-empty songUid={songUid}");
            return false;
        }

        var usableSubtitles = NormalizeSubtitleCount(subtitles, snapshot.Lyrics.Count);
        AppendTrace($"official-subtitle-timing songUid={songUid}; subtitleCount={subtitles.Count}; usableCount={usableSubtitles.Count}; lyricCount={snapshot.Lyrics.Count}");
        if (usableSubtitles.Count != snapshot.Lyrics.Count)
        {
            if (!TryBuildTextAnchoredOfficialSubtitleFixedOffset(
                snapshot.Lyrics,
                subtitles,
                out var textAnchoredSegments,
                out var evidenceCount,
                out var fixedOffsetSeconds))
            {
                return false;
            }

            await songPersistence.UpdateSongTranslationsAsync(
                songUid,
                textAnchoredSegments,
                snapshot.Lyrics.Select(lyric => lyric.LyricID).ToList(),
                cancellationToken);
            await SetHighAccuracyStatusSafeAsync(
                songUid,
                "high_accuracy_completed",
                $"YouTube 官方字幕文字錨點驗證穩定，已套用全曲固定偏移 {fixedOffsetSeconds:F2} 秒，共 {textAnchoredSegments.Count} 行，錨點 {evidenceCount} 個。",
                cancellationToken);
            return true;
        }

        var alignedSegments = new List<LyricSegment>(snapshot.Lyrics.Count);
        double previousTimestamp = -1;
        for (var i = 0; i < snapshot.Lyrics.Count; i++)
        {
            var subtitle = usableSubtitles[i];
            var timestamp = Math.Round(subtitle.TimeStamp, 2, MidpointRounding.AwayFromZero);
            if (timestamp < previousTimestamp)
            {
                AppendTrace($"official-subtitle-timing-rejected songUid={songUid}; reason=non-monotonic; index={i}");
                return false;
            }

            previousTimestamp = timestamp;
            alignedSegments.Add(new LyricSegment
            {
                LyricID = snapshot.Lyrics[i].LyricID,
                TimeStamp = timestamp,
                Japanese = snapshot.Lyrics[i].Japanese,
                Chinese = string.IsNullOrWhiteSpace(subtitle.Japanese)
                    ? snapshot.Lyrics[i].Chinese
                    : subtitle.Japanese,
                JapaneseRuby = snapshot.Lyrics[i].JapaneseRuby,
                Roman = snapshot.Lyrics[i].Roman
            });
        }

        await songPersistence.UpdateSongTranslationsAsync(
            songUid,
            alignedSegments,
            snapshot.Lyrics.Select(lyric => lyric.LyricID).ToList(),
            cancellationToken);
        await SetHighAccuracyStatusSafeAsync(
            songUid,
            "high_accuracy_completed",
            $"已採用 YouTube 官方字幕時間軸完成逐句校正，共 {alignedSegments.Count} 行。",
            cancellationToken);
        return true;
    }

    private static IReadOnlyList<LyricSegment> NormalizeSubtitleCount(IReadOnlyList<LyricSegment> subtitles, int lyricCount)
    {
        if (subtitles.Count == lyricCount)
        {
            return subtitles;
        }

        if (subtitles.Count == lyricCount + 1)
        {
            return subtitles.Take(lyricCount).ToList();
        }

        return subtitles;
    }

    private static bool TryBuildTextAnchoredOfficialSubtitleFixedOffset(
        IReadOnlyList<LyricSegment> lyrics,
        IReadOnlyList<LyricSegment> subtitles,
        out List<LyricSegment> alignedSegments,
        out int evidenceCount,
        out double fixedOffsetSeconds)
    {
        alignedSegments = new List<LyricSegment>();
        evidenceCount = 0;
        fixedOffsetSeconds = 0;
        if (lyrics.Count < 4 || subtitles.Count < 4)
        {
            return false;
        }

        var offsets = new List<double>();
        var nextSubtitleIndex = 0;
        for (var lyricIndex = 0; lyricIndex < lyrics.Count; lyricIndex++)
        {
            var matchedSubtitleIndex = FindNextSubtitleTextAnchor(
                lyrics[lyricIndex].Japanese,
                subtitles,
                nextSubtitleIndex,
                lyricIndex,
                lyrics.Count);
            if (matchedSubtitleIndex < 0)
            {
                continue;
            }

            var offset = subtitles[matchedSubtitleIndex].TimeStamp - lyrics[lyricIndex].TimeStamp;
            if (Math.Abs(offset) <= 120)
            {
                offsets.Add(offset);
            }

            nextSubtitleIndex = matchedSubtitleIndex + 1;
        }

        evidenceCount = offsets.Count;
        var requiredEvidence = Math.Max(6, Math.Min(18, (int)Math.Ceiling(lyrics.Count * 0.30)));
        if (offsets.Count < requiredEvidence)
        {
            return false;
        }

        offsets.Sort();
        var medianOffset = Median(offsets);
        var medianAbsoluteDeviation = Median(offsets
            .Select(offset => Math.Abs(offset - medianOffset))
            .OrderBy(value => value)
            .ToList());
        if (Math.Abs(medianOffset) > 60 || medianAbsoluteDeviation > 0.65)
        {
            return false;
        }

        fixedOffsetSeconds = Math.Round(medianOffset, 2, MidpointRounding.AwayFromZero);
        double previousTimestamp = -1;
        foreach (var lyric in lyrics)
        {
            var timestamp = Math.Round(Math.Max(0, lyric.TimeStamp + fixedOffsetSeconds), 2, MidpointRounding.AwayFromZero);
            if (timestamp < previousTimestamp)
            {
                return false;
            }

            previousTimestamp = timestamp;
            alignedSegments.Add(new LyricSegment
            {
                LyricID = lyric.LyricID,
                TimeStamp = timestamp,
                Japanese = lyric.Japanese,
                Chinese = lyric.Chinese,
                JapaneseRuby = lyric.JapaneseRuby,
                Roman = lyric.Roman
            });
        }

        return true;
    }

    private static int FindNextSubtitleTextAnchor(
        string? lyricText,
        IReadOnlyList<LyricSegment> subtitles,
        int nextSubtitleIndex,
        int lyricIndex,
        int lyricCount)
    {
        var windowEnd = Math.Min(subtitles.Count, nextSubtitleIndex + 8);
        for (var subtitleIndex = nextSubtitleIndex; subtitleIndex < windowEnd; subtitleIndex++)
        {
            if (IsSubtitleTextAnchorMatch(lyricText, subtitles[subtitleIndex].Japanese))
            {
                return subtitleIndex;
            }
        }

        var expectedIndex = (int)Math.Round((double)lyricIndex * subtitles.Count / Math.Max(1, lyricCount));
        var fallbackStart = Math.Max(nextSubtitleIndex, expectedIndex - 4);
        var fallbackEnd = Math.Min(subtitles.Count, expectedIndex + 8);
        for (var subtitleIndex = fallbackStart; subtitleIndex < fallbackEnd; subtitleIndex++)
        {
            if (IsSubtitleTextAnchorMatch(lyricText, subtitles[subtitleIndex].Japanese))
            {
                return subtitleIndex;
            }
        }

        return -1;
    }

    private static bool IsSubtitleTextAnchorMatch(string? lyricText, string? subtitleText)
    {
        var lyric = NormalizeSubtitleAnchorText(lyricText);
        var subtitle = NormalizeSubtitleAnchorText(subtitleText);
        if (lyric.Length == 0 || subtitle.Length == 0)
        {
            return false;
        }

        if (string.Equals(lyric, subtitle, StringComparison.Ordinal))
        {
            return true;
        }

        var shorterLength = Math.Min(lyric.Length, subtitle.Length);
        return shorterLength >= 4
            && (lyric.Contains(subtitle, StringComparison.Ordinal)
                || subtitle.Contains(lyric, StringComparison.Ordinal));
    }

    private static string NormalizeSubtitleAnchorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\([^)]*\)|（[^）]*）", string.Empty);
        return Regex.Replace(normalized, @"[\s\p{P}\p{S}]+", string.Empty).Trim();
    }

    private static bool TryBuildAlignedCompleteSnapshot(
        IReadOnlyList<LyricSegment> lyrics,
        IReadOnlyList<VocalOnsetDetectionService.LyricTimingAlignment> alignments,
        out List<LyricSegment> alignedSegments,
        out int matchedCount)
    {
        alignedSegments = new List<LyricSegment>();
        matchedCount = 0;
        if (lyrics.Count != alignments.Count)
        {
            return false;
        }

        double previousTimestamp = -1;
        for (var i = 0; i < lyrics.Count; i++)
        {
            var alignment = alignments[i];
            if (!alignment.IsMatched)
            {
                alignedSegments.Clear();
                return false;
            }

            var timestamp = Math.Round(alignment.Start, 2, MidpointRounding.AwayFromZero);
            if (timestamp < previousTimestamp)
            {
                alignedSegments.Clear();
                matchedCount = 0;
                return false;
            }

            previousTimestamp = timestamp;
            matchedCount++;
            alignedSegments.Add(new LyricSegment
            {
                LyricID = lyrics[i].LyricID,
                TimeStamp = timestamp,
                Japanese = lyrics[i].Japanese,
                Chinese = lyrics[i].Chinese,
                JapaneseRuby = lyrics[i].JapaneseRuby,
                Roman = lyrics[i].Roman
            });
        }

        return matchedCount == lyrics.Count;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2;
    }

    public void EnqueueHighAccuracyInitialPass(string songUid)
    {
        if (!QueuedOrRunningSongUids.TryAdd(songUid, 0))
        {
            AppendTrace($"enqueue-skip:already-queued songUid={songUid}");
            return;
        }

        HighAccuracyQueue.Enqueue(songUid);
        AppendTrace($"enqueue songUid={songUid}");
        StartQueueWorkerIfNeeded();
    }

    private void StartQueueWorkerIfNeeded()
    {
        if (Interlocked.CompareExchange(ref QueueWorkerStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(ProcessQueuedHighAccuracyRunsAsync);
    }

    private async Task ProcessQueuedHighAccuracyRunsAsync()
    {
        try
        {
            while (HighAccuracyQueue.TryDequeue(out var songUid))
            {
                await HighAccuracyQueueGate.WaitAsync();
                try
                {
                    await RunHighAccuracyInitialPassAsync(songUid);
                }
                catch (Exception ex)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var songPersistence = scope.ServiceProvider.GetRequiredService<IWhisperSongPersistenceService>();
                        await songPersistence.UpdateHighAccuracyStatusAsync(songUid, "high_accuracy_failed", UnhandledExceptionReason);
                    }
                    catch
                    {
                    }
                    _logger.LogError(ex, "背景高精度初始分句失敗 songUid={SongUid}", songUid);
                    AppendTrace($"failed songUid={songUid}; error={ex.Message}");
                }
                finally
                {
                    QueuedOrRunningSongUids.TryRemove(songUid, out _);
                    HighAccuracyQueueGate.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref QueueWorkerStarted, 0);
            if (!HighAccuracyQueue.IsEmpty)
            {
                StartQueueWorkerIfNeeded();
            }
        }
    }

    private static void CleanupFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private async Task SetHighAccuracyStatusSafeAsync(string songUid, string? highAccuracyStatus, string? highAccuracyStatusReason, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var songPersistence = scope.ServiceProvider.GetRequiredService<IWhisperSongPersistenceService>();
            await songPersistence.UpdateHighAccuracyStatusAsync(songUid, highAccuracyStatus, highAccuracyStatusReason, cancellationToken);
            AppendTrace($"high-accuracy-status songUid={songUid}; value={(highAccuracyStatus ?? "<null>")}; reason={(highAccuracyStatusReason ?? "<null>")}");
        }
        catch (Exception ex)
        {
            AppendTrace($"high-accuracy-status-failed songUid={songUid}; value={(highAccuracyStatus ?? "<null>")}; reason={(highAccuracyStatusReason ?? "<null>")}; error={ex.Message}");
        }
    }

    private static bool HasCompleteChineseTranslations(IReadOnlyList<LyricSegment> segments)
        => segments.Count > 0 && segments.All(segment =>
            !string.IsNullOrWhiteSpace(segment.Chinese)
            && !string.Equals(segment.Chinese.Trim(), "翻譯中...", StringComparison.Ordinal));

    private async Task<double?> TryGetAudioDurationSecondsAsync(string? audioFilePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            return null;
        }

        var ffprobePath = ResolveFfprobePath(_options.FfmpegPath);
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{audioFilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);
            if (double.TryParse(
                output.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var duration)
                && duration > 0)
            {
                return duration;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendTrace($"audio-duration-failed path={audioFilePath}; error={ex.Message}");
        }

        return null;
    }

    private static string ResolveFfprobePath(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)
            || string.Equals(ffmpegPath, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        }

        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        }

        return Path.Combine(directory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe");
    }

    private static bool AreTimestampsWithinAudioDuration(
        IReadOnlyList<LyricSegment> segments,
        double? audioDurationSeconds,
        out string reason)
    {
        reason = string.Empty;
        if (segments.Count == 0 || !audioDurationSeconds.HasValue)
        {
            return true;
        }

        var minTimestamp = segments.Min(segment => segment.TimeStamp);
        var maxTimestamp = segments.Max(segment => segment.TimeStamp);
        if (minTimestamp < -0.5)
        {
            reason = $"最小秒數 {minTimestamp:F2}s 小於 0";
            return false;
        }

        var allowedMax = audioDurationSeconds.Value + 5.0;
        if (maxTimestamp > allowedMax)
        {
            reason = $"最大秒數 {maxTimestamp:F2}s 超過音訊長度 {audioDurationSeconds.Value:F2}s";
            return false;
        }

        return true;
    }

    private static bool HasCompleteSnapshotLyrics(IReadOnlyList<LyricSegment> segments)
        => segments.Count > 0 && segments.All(segment =>
            segment.TimeStamp >= 0
            && !string.IsNullOrWhiteSpace(segment.Japanese)
            && !string.IsNullOrWhiteSpace(segment.Chinese)
            && !string.Equals(segment.Chinese.Trim(), "翻譯中...", StringComparison.Ordinal));

    private static bool HasAdequateLyricCoverage(IReadOnlyList<LyricSegment> candidateSegments, IReadOnlyList<LyricSegment> existingLyrics)
    {
        var existingLines = existingLyrics
            .Select(segment => NormalizeLyricText(segment.Japanese))
            .Where(line => line.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (existingLines.Count < 8)
            return true;

        var candidateLines = candidateSegments
            .Select(segment => NormalizeLyricText(segment.Japanese))
            .Where(line => line.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidateLines.Count == 0)
            return false;

        if (candidateLines.Count < Math.Max(8, (int)Math.Ceiling(existingLines.Count * 0.5)))
            return false;

        var matched = existingLines.Count(existing => candidateLines.Any(candidate =>
            candidate.Contains(existing, StringComparison.Ordinal)
            || existing.Contains(candidate, StringComparison.Ordinal)));
        var required = Math.Max(3, (int)Math.Ceiling(existingLines.Count * 0.25));
        return matched >= required;
    }

    private static bool TryGetCandidateLyricsQualityIssue(
        IReadOnlyList<LyricSegment> candidateSegments,
        IReadOnlyList<LyricSegment> existingLyrics,
        out string reason)
    {
        reason = string.Empty;
        if (candidateSegments.Count == 0)
        {
            reason = NoSegmentsReason;
            return true;
        }

        var suspiciousCompressedAsciiCount = candidateSegments.Count(segment =>
            HasSuspiciousCompressedAscii(segment.Japanese));
        var suspiciousThreshold = Math.Max(3, (int)Math.Ceiling(candidateSegments.Count * 0.08));
        if (suspiciousCompressedAsciiCount >= suspiciousThreshold)
        {
            reason = $"疑似英文詞被壓縮成一串的分句過多（{suspiciousCompressedAsciiCount}/{candidateSegments.Count}）";
            return true;
        }

        if (existingLyrics.Count >= 8)
        {
            var existingJapaneseRatio = CalculateJapaneseScriptRatio(existingLyrics);
            var candidateJapaneseRatio = CalculateJapaneseScriptRatio(candidateSegments);
            if (existingJapaneseRatio >= 0.4 && candidateJapaneseRatio < Math.Max(0.25, existingJapaneseRatio * 0.5))
            {
                reason = $"日文假名/漢字覆蓋率異常偏低（候選 {candidateJapaneseRatio:P0}，既有 {existingJapaneseRatio:P0}）";
                return true;
            }
        }

        return false;
    }

    private static double CalculateJapaneseScriptRatio(IReadOnlyList<LyricSegment> segments)
    {
        if (segments.Count == 0)
        {
            return 0;
        }

        var japaneseLineCount = segments.Count(segment => ContainsJapaneseScript(segment.Japanese));
        return japaneseLineCount / (double)segments.Count;
    }

    private static bool ContainsJapaneseScript(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && Regex.IsMatch(text, @"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant);

    private static bool HasSuspiciousCompressedAscii(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = Regex.Replace(text, @"<[^>]+>", string.Empty);
        foreach (Match match in Regex.Matches(normalized, @"[A-Za-z]{10,}", RegexOptions.CultureInvariant))
        {
            var value = match.Value;
            if (ContainsJapaneseScript(normalized) || LooksLikeCollapsedEnglishPhrase(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeCollapsedEnglishPhrase(string value)
    {
        if (value.Length < 10)
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        string[] commonFragments =
        {
            "the", "and", "you", "your", "with", "that", "this", "dawn",
            "light", "shining", "brand", "break", "learn", "fall", "view",
            "had", "to", "on", "me",
            "okay", "coming", "going"
        };

        return commonFragments.Count(fragment => lower.Contains(fragment, StringComparison.Ordinal)) >= 2;
    }

    private static string NormalizeLyricText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : Regex.Replace(text, @"[\s\p{P}\p{S}]+", string.Empty).Trim();

    private string ResolveFastLocalFallbackModel()
    {
        var fallbackModel = string.IsNullOrWhiteSpace(_options.InitialSegmentationHighAccuracyFallbackModel)
            ? "tiny"
            : _options.InitialSegmentationHighAccuracyFallbackModel.Trim();
        var primaryModel = _options.InitialSegmentationHighAccuracyModel?.Trim() ?? string.Empty;
        return string.Equals(fallbackModel, primaryModel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : fallbackModel;
    }

    private static bool ShouldRetryWithFastLocalModel(string? failureReason, string fallbackModel)
    {
        if (string.IsNullOrWhiteSpace(fallbackModel))
            return false;

        return failureReason is "local_faster_whisper_timeout" or "local_faster_whisper_empty_words";
    }

    private static bool IsPermanentYouTubeDownloadFailure(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Private video", StringComparison.OrdinalIgnoreCase)
            || message.Contains("has been removed", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHighAccuracyFailureReason(string? failureReason, string? failedModel)
    {
        var modelName = string.IsNullOrWhiteSpace(failedModel) ? "未指定" : failedModel.Trim();
        return failureReason switch
        {
            "local_faster_whisper_timeout" => $"高精度語音辨識逾時（模型 {modelName}）",
            "local_faster_whisper_process_failed" => "高精度本機語音辨識程序失敗",
            "local_faster_whisper_no_json" => "高精度本機語音辨識未輸出結果",
            "local_faster_whisper_parse_failed" => "高精度本機語音辨識結果解析失敗",
            "local_faster_whisper_empty_words" => "高精度語音辨識未產出可用字詞",
            "audio_file_missing" => DownloadEmptyReason,
            "initial_segment_build_empty" => NoSegmentsReason,
            _ => NoSegmentsReason
        };
    }

    private static void AppendTrace(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(HighAccuracyTracePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(HighAccuracyTracePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
