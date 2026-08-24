using Xunit;

namespace LearnMore.Tests;

public class UploadQualityStatusMvpTests
{
    [Fact]
    public void HighAccuracyStatusSummary_ShouldRenderOnlyActionableStatusesAndHideCompletedBadge()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_HighAccuracyStatusSummary.cshtml"));

        Assert.Contains("high_accuracy_partial", source);
        Assert.Contains("秒數待確認", source);
        Assert.Contains("high_accuracy_needs_review", source);
        Assert.Contains("需要人工校正", source);
        Assert.Contains("bg-info-subtle text-info-emphasis", source);
        Assert.DoesNotContain("高精度已完成", source);
        Assert.DoesNotContain("high_accuracy_completed\" =>", source);
    }

    [Fact]
    public void WebTranscribe_ShouldNotMarkSyncedLyricsCompletedWhenPrecisionCorrectionIsPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var syncedBranchIndex = source.IndexOf("else if (fromLrcLibOrNetEase)", StringComparison.Ordinal);
        var marumaruBranchIndex = source.IndexOf("else if (preAlignedSegments is { Count: > 0 })", StringComparison.Ordinal);

        Assert.True(syncedBranchIndex >= 0, "WebTranscribe should handle synced lyric sources.");
        Assert.True(marumaruBranchIndex > syncedBranchIndex, "The synced branch should be bounded before the marumaru branch.");

        var syncedBranch = source[syncedBranchIndex..marumaruBranchIndex];
        Assert.Contains("precisionCorrectionCompleted", syncedBranch);
        Assert.Contains("precisionCorrectionReviewReason", syncedBranch);
        Assert.Contains("\"high_accuracy_completed\"", syncedBranch);
        Assert.Contains("\"high_accuracy_pending\"", syncedBranch);
        Assert.Contains("timing_validation_pending", syncedBranch);
        Assert.Contains("已保留同步歌詞來源時間戳", syncedBranch);
        Assert.Contains("shouldRunBackgroundHighAccuracyPass = true;", syncedBranch);
        Assert.Contains("背景高精度補跑中", syncedBranch);
    }

    [Fact]
    public void WebTranscribe_ShouldNotMarkCompletedWhenTranslationsArePlaceholders()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var translationGuardIndex = source.IndexOf("HasCompleteChineseTranslations(finalSegments)", StringComparison.Ordinal);
        var completedStatusIndex = source.IndexOf("else if (fromLrcLibOrNetEase)", StringComparison.Ordinal);

        Assert.True(translationGuardIndex >= 0, "WebTranscribe should guard incomplete translations.");
        Assert.True(completedStatusIndex >= 0, "WebTranscribe should still complete clean synced lyrics.");
        Assert.True(translationGuardIndex < completedStatusIndex, "Incomplete translations must be rejected before completed status is written.");
        Assert.Contains("\"translation_pending_codex\"", source);
        Assert.Contains("後台補件", source);
        Assert.DoesNotContain("\"translation_incomplete\"", source);
    }

    [Fact]
    public void WebTranscribe_ShouldNotPersistYouTubeAutoCaptionTextWithoutFormalLyrics()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var autoCaptionGuardIndex = source.IndexOf(
            "fromYouTubeAutoCaptionTimeAnchors && preAlignedSegments is not { Count: > 0 }",
            StringComparison.Ordinal);
        var insertSegmentsIndex = source.IndexOf(
            "List<LyricSegment> segmentsToInsert = preAlignedSegments ?? timestampSegments",
            StringComparison.Ordinal);

        Assert.True(autoCaptionGuardIndex >= 0, "WebTranscribe should reject auto-caption anchors when no formal lyrics match.");
        Assert.True(insertSegmentsIndex >= 0, "WebTranscribe should still have a single segment insertion decision.");
        Assert.True(autoCaptionGuardIndex < insertSegmentsIndex, "The auto-caption guard must run before choosing segments to persist.");
        Assert.Contains("未找到可對齊的正式歌詞", source);
        Assert.Contains("自動字幕聽寫內容", source);
        Assert.Contains("return new EmptyResult();", source);
    }

    [Fact]
    public void HighAccuracyStatusSummary_ShouldShowCodexTranslationQueueBadge()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Shared",
            "_HighAccuracyStatusSummary.cshtml"));

        Assert.Contains("\"translation_pending_codex\" => \"翻譯待補\"", source);
        Assert.Contains("\"translation_pending_codex\" => \"bg-primary text-white border border-primary\"", source);
    }

    [Fact]
    public void HomeIndex_ShouldOnlyShowPublicProcessingBadges()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Home",
            "Index.cshtml"));

        Assert.Contains("publicHomeBadgeStatuses", source);
        Assert.Contains("\"high_accuracy_pending\"", source);
        Assert.Contains("\"high_accuracy_processing\"", source);
        Assert.Contains("publicHomeBadgeStatuses.Contains(song.HighAccuracyStatus ?? string.Empty)", source);
        Assert.DoesNotContain("\"high_accuracy_needs_review\"", source);
        Assert.DoesNotContain("\"high_accuracy_failed\"", source);
    }

    [Fact]
    public void WebTranscribe_ShouldAutoApplyStableFixedOffsetBeforeCompletingSyncedLyrics()
    {
        var controllerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));
        var vocalSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        Assert.Contains("stable_fixed_offset", vocalSource);
        Assert.Contains("TryBuildStableFixedOffsetAlignment", vocalSource);
        Assert.Contains("FixedOffsetSeconds", controllerSource);
        Assert.Contains("全曲固定偏移", controllerSource);
    }

    [Fact]
    public void CompleteSnapshotAlignment_ShouldProbeStableFixedOffsetBeforeSecondaryWindows()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        var firstStableProbeIndex = source.IndexOf("TryApplyStableFixedOffsetAlignment(", StringComparison.Ordinal);
        var secondaryHintsIndex = source.IndexOf("TryApplySecondaryAlignmentHintsAsync", StringComparison.Ordinal);

        Assert.True(firstStableProbeIndex >= 0, "Alignment should probe stable fixed offset.");
        Assert.True(secondaryHintsIndex >= 0, "Alignment should still have secondary window fallback.");
        Assert.True(firstStableProbeIndex < secondaryHintsIndex, "Stable fixed offset must run before slower secondary windows consume the timeout budget.");
        Assert.Contains("return BuildAlignmentAttemptResult(", source);
    }

    [Fact]
    public void WebTranscribe_ShouldGiveLocalPrecisionAlignmentEnoughTimeWithoutOpenAiFallback()
    {
        var controllerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));
        var vocalSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        Assert.Contains("CancelAfter(TimeSpan.FromSeconds(300))", controllerSource);
        Assert.DoesNotContain("CancelAfter(TimeSpan.FromSeconds(120))", controllerSource);
        Assert.DoesNotContain("CancelAfter(TimeSpan.FromSeconds(180))", controllerSource);
        Assert.Contains("allowOpenAiFallback: false", vocalSource);
        Assert.Contains("catch (OperationCanceledException)", vocalSource);
        Assert.DoesNotContain("openai_http_failed：A task was canceled.", controllerSource);
    }

    [Fact]
    public void LocalFasterWhisper_ShouldDefaultToLongTimeoutForFullSongHighAccuracyPass()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        Assert.Contains("LocalFasterWhisperTimeoutSeconds", source);
        Assert.Contains("?? 600", source);
        Assert.DoesNotContain("?? 180", source);
    }

    [Fact]
    public void LocalFasterWhisper_ShouldBoundOutputReadsAfterTimeout()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        Assert.Contains("ReadProcessOutputWithTimeoutAsync", source);
        Assert.Contains("WaitForProcessExitAfterKillAsync", source);
        Assert.Contains("process output read timed out", source);
        Assert.DoesNotContain("var timeoutStdout = await stdoutTask;", source);
        Assert.DoesNotContain("var timeoutStderr = await stderrTask;", source);
    }

    [Fact]
    public void SecondaryAlignmentWindow_ShouldHaveIndependentTimeout()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "VocalOnsetDetectionService.cs"));

        Assert.Contains("SecondaryAlignmentWindowTimeoutSeconds", source);
        Assert.Contains("?? 120", source);
        Assert.Contains("AppendSecondaryAlignmentTrace($\"timeout:", source);
        Assert.Contains("timeout-recovered-json", source);
        Assert.DoesNotContain("process.StandardOutput.ReadToEndAsync(ct)", source);
        Assert.DoesNotContain("process.StandardError.ReadToEndAsync(ct)", source);
    }

    [Fact]
    public void HighAccuracyPass_ShouldRetryFastLocalFallbackBeforeFailingTimedOutPrimaryModel()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperHighAccuracyInitialPassService.cs"));

        Assert.Contains("FastLocalModelFallbackReason", source);
        Assert.Contains("ShouldRetryWithFastLocalModel", source);
        Assert.Contains("local_faster_whisper_timeout", source);
        Assert.Contains("\"tiny\"", source);
    }

    [Fact]
    public void ManualUploadPersistence_ShouldGenerateSongUidBeforeInsertingSongsRow()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperSongPersistenceService.cs"));

        Assert.Contains("var songUid = Guid.NewGuid().ToString();", source);
        Assert.Contains("INSERT INTO [Songs] ([SongUid], [Title], [Artist], [Performer], [YouTubeVideoUrl])", source);
        Assert.Contains("command.Parameters.AddWithValue(\"@SongUid\", songUid);", source);
    }

    [Fact]
    public void UploadView_ShouldNotDisplayRawHtmlErrorPagesInAlerts()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Upload.cshtml"));

        Assert.Contains("readFriendlyErrorMessage", source);
        Assert.Contains("content-type", source);
        Assert.DoesNotContain("throw new Error(`新增失敗: ${errorText}`);", source);
        Assert.DoesNotContain("throw new Error(`辨識失敗: ${errorText}`);", source);
    }

    [Fact]
    public void UpdateTranscription_ShouldValidateSegmentsBeforeCreatingSong()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var validationIndex = source.IndexOf("HasUsableManualSegments(request.Segments)", StringComparison.Ordinal);
        var insertIndex = source.IndexOf("AddSongToDatabaseAsync(request.SongData)", StringComparison.Ordinal);

        Assert.True(validationIndex >= 0, "UpdateTranscription should validate usable segments.");
        Assert.True(insertIndex >= 0, "UpdateTranscription should create the Songs row.");
        Assert.True(validationIndex < insertIndex, "Segments must be validated before creating DB rows.");
    }

    [Fact]
    public void Summon_ShouldValidateLyricsBeforeCreatingSong()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var validationIndex = source.IndexOf("HasUsableSummonLyrics(request.Lyrics)", StringComparison.Ordinal);
        var createIndex = source.IndexOf("CreateSummonedSongAsync(request, preparedLyrics", StringComparison.Ordinal);

        Assert.True(validationIndex >= 0, "Summon should validate usable lyrics server-side.");
        Assert.True(createIndex >= 0, "Summon should create the song through persistence.");
        Assert.True(validationIndex < createIndex, "Summon lyrics must be validated before creating DB rows.");
    }

    [Fact]
    public void WebTranscribe_ShouldLinkProducerBeforeDoneEvent()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var webTranscribeStart = source.IndexOf("public async Task<IActionResult> WebTranscribe", StringComparison.Ordinal);
        Assert.True(webTranscribeStart >= 0, "WebTranscribe action should exist.");

        var webTranscribeSource = source[webTranscribeStart..];
        var appendIndex = webTranscribeSource.IndexOf("AppendProducerSongAsync(userEmail, songUid, CancellationToken.None)", StringComparison.Ordinal);
        var doneIndex = webTranscribeSource.IndexOf("await SendEvent(\"done\", new { songUid, redirectUrl = Url.Action(\"Manage\", \"Media\") });", StringComparison.Ordinal);

        Assert.True(appendIndex >= 0, "WebTranscribe should link the song to the uploader.");
        Assert.True(doneIndex >= 0, "WebTranscribe should send done event.");
        Assert.True(appendIndex < doneIndex, "The song must be visible in Manage before the client receives done.");
        Assert.DoesNotContain("AppendProducerSongAsync(userEmail, songUid, ct)", webTranscribeSource);
    }

    [Fact]
    public void UploadView_ShouldRedirectAfterAutomaticSongCreationToAvoidDuplicateManualSubmit()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Upload.cshtml"));

        Assert.Contains("window.location.href = data.redirectUrl;", source);
        Assert.Contains("document.getElementById(\"submitChanges\").disabled = true;", source);
        Assert.Contains("已建立歌曲", source);
    }

    [Fact]
    public void WebTranscribe_ShouldNotReturnInternalExceptionDetailsToClient()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        Assert.Contains("請稍後再試或通知管理員查看伺服器紀錄", source);
        Assert.DoesNotContain("ex.GetType().Name", source);
        Assert.DoesNotContain("ex.InnerException.Message", source);
    }

    [Fact]
    public void ManageView_ShouldLinkToReviewQueueForSongsNeedingTimingReview()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "Manage.cshtml"));

        Assert.Contains("ReviewQueue", source);
        Assert.Contains("待校正清單", source);
    }

    [Fact]
    public void ReviewQueue_ShouldFilterSongsThatNeedTimingReview()
    {
        var controllerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        var viewSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Views",
            "Media",
            "ReviewQueue.cshtml"));

        Assert.Contains("public async Task<IActionResult> ReviewQueue()", controllerSource);
        Assert.Contains("NeedsTimingReview", controllerSource);
        Assert.Contains("high_accuracy_partial", controllerSource);
        Assert.Contains("high_accuracy_needs_review", controllerSource);
        Assert.Contains("high_accuracy_failed", controllerSource);
        Assert.Contains("需要確認秒數的歌曲", viewSource);
        Assert.Contains("校正歌詞秒數", viewSource);
    }

    [Fact]
    public void MediaController_ShouldExposeAuthenticatedHighAccuracyRetryQueue()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Controllers",
            "MediaController.cs"));

        Assert.Contains("RetryHighAccuracyQueue", source);
        Assert.Contains("IncludeAll", source);
        Assert.Contains("GetAllRetryableHighAccuracySongsForMaintenanceAsync", source);
        Assert.Contains("IsRetryableHighAccuracyStatus", source);
        Assert.Contains("\"high_accuracy_processing\"", source);
        Assert.Contains("\"high_accuracy_needs_review\"", source);
        Assert.Contains("\"high_accuracy_failed\"", source);
        Assert.Contains("_highAccuracyInitialPassService.EnqueueHighAccuracyInitialPass", source);
    }

    [Fact]
    public void HighAccuracyInitialPassService_ShouldSerializeQueuedBackgroundRuns()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperHighAccuracyInitialPassService.cs"));

        Assert.Contains("SemaphoreSlim", source);
        Assert.Contains("ConcurrentQueue<string>", source);
        Assert.Contains("HighAccuracyQueueGate.WaitAsync", source);
        Assert.Contains("HighAccuracyQueueGate.Release", source);
        Assert.Contains("ProcessQueuedHighAccuracyRunsAsync", source);
        Assert.Contains("Interlocked.CompareExchange", source);
    }

    [Fact]
    public void HighAccuracyInitialPassService_ShouldDeduplicateQueuedBackgroundRuns()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperHighAccuracyInitialPassService.cs"));

        Assert.Contains("ConcurrentDictionary<string, byte>", source);
        Assert.Contains("QueuedOrRunningSongUids.TryAdd", source);
        Assert.Contains("enqueue-skip:already-queued", source);
        Assert.Contains("QueuedOrRunningSongUids.TryRemove", source);
    }

    [Fact]
    public void HighAccuracyQueueRecoveryHostedService_ShouldResumeRetryableSongs()
    {
        var programSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Program.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "HighAccuracyQueueRecoveryHostedService.cs"));

        Assert.Contains("AddHostedService<HighAccuracyQueueRecoveryHostedService>", programSource);
        Assert.Contains("PeriodicTimer", serviceSource);
        Assert.Contains("HighAccuracyQueueRecoveryIntervalMinutes", serviceSource);
        Assert.Contains("GetRetryableHighAccuracySongsAsync", serviceSource);
        Assert.Contains("EnqueueHighAccuracyInitialPass", serviceSource);
        Assert.Contains("includeNeedsReview: false", serviceSource);
        Assert.Contains("queue-recovery", serviceSource);

        var querySource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperLyricsQueryService.cs"));
        Assert.Contains("已停止自動補跑", querySource);
    }

    [Fact]
    public void HighAccuracyInitialPassService_ShouldAlignCompleteSnapshotBeforeReviewFallback()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LearnMore",
            "Services",
            "WhisperHighAccuracyInitialPassService.cs"));

        Assert.Contains("TryAlignCompleteSnapshotLyricsAsync", source);
        Assert.Contains("complete-snapshot-alignment", source);
        Assert.Contains("完整同步歌詞經同影片 ASR 驗證", source);
        Assert.Contains("\"high_accuracy_needs_review\"", source);
    }
}
