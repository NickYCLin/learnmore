using LearnMore.Controllers.API;
using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LearnMore.Controllers
{
    public class MediaController : Controller
    {
        #region 基本參數
        private readonly YtDlpAudioDownloaderService _audioDownloader;
        private readonly IOpenAiWhisperClientService _openAiWhisperClient;
        private readonly WhisperTranscriptionPersistenceService _transcriptionPersistence;
        private readonly IWhisperSongPersistenceService _songPersistence;
        private readonly IWhisperPostProcessService _postProcessService;
        private readonly IWhisperSummonPreparationService _summonPreparationService;
        private readonly IWhisperManageQueryService _manageQueryService;
        private readonly IWhisperEditQueryService _editQueryService;
        private readonly IWhisperEditMutationService _editMutationService;
        private readonly IWhisperLyricsQueryService _lyricsQueryService;
        private readonly IWhisperLyricsMutationService _lyricsMutationService;
        private readonly IWhisperTranslationSourceService _translationSourceService;
        private readonly IYouTubeMetadataResolverService _youTubeMetadataResolverService;
        private readonly IWhisperAudioPreprocessService _audioPreprocessService;
        private readonly LrcLibService _lrcLibService;
        private readonly NetEaseLrcService _netEaseLrcService;
        private readonly TypingTubeLyricsService _typingTubeLyricsService;
        private readonly IYouTubeSubtitleDownloadService _youTubeSubtitleDownloadService;
        private readonly VocalOnsetDetectionService _vocalOnsetService;
        private readonly IWhisperHighAccuracyInitialPassService _highAccuracyInitialPassService;
        private readonly ILogger<MediaController> _logger;
        private const double MaxAutomaticTranscriptionDurationSeconds = 360.0;

        public MediaController(YtDlpAudioDownloaderService audioDownloader, IOpenAiWhisperClientService openAiWhisperClient, WhisperTranscriptionPersistenceService transcriptionPersistence, IWhisperSongPersistenceService songPersistence, IWhisperPostProcessService postProcessService, IWhisperSummonPreparationService summonPreparationService, IWhisperManageQueryService manageQueryService, IWhisperEditQueryService editQueryService, IWhisperEditMutationService editMutationService, IWhisperLyricsQueryService lyricsQueryService, IWhisperLyricsMutationService lyricsMutationService, IWhisperTranslationSourceService translationSourceService, IYouTubeMetadataResolverService youTubeMetadataResolverService, IWhisperAudioPreprocessService audioPreprocessService, LrcLibService lrcLibService, NetEaseLrcService netEaseLrcService, TypingTubeLyricsService typingTubeLyricsService, IYouTubeSubtitleDownloadService youTubeSubtitleDownloadService, VocalOnsetDetectionService vocalOnsetService, IWhisperHighAccuracyInitialPassService highAccuracyInitialPassService, ILogger<MediaController> logger)
        {
            _audioDownloader = audioDownloader;
            _openAiWhisperClient = openAiWhisperClient;
            _transcriptionPersistence = transcriptionPersistence;
            _songPersistence = songPersistence;
            _postProcessService = postProcessService;
            _summonPreparationService = summonPreparationService;
            _manageQueryService = manageQueryService;
            _editQueryService = editQueryService;
            _editMutationService = editMutationService;
            _lyricsQueryService = lyricsQueryService;
            _lyricsMutationService = lyricsMutationService;
            _translationSourceService = translationSourceService;
            _youTubeMetadataResolverService = youTubeMetadataResolverService;
            _audioPreprocessService = audioPreprocessService;
            _lrcLibService = lrcLibService;
            _netEaseLrcService = netEaseLrcService;
            _typingTubeLyricsService = typingTubeLyricsService;
            _youTubeSubtitleDownloadService = youTubeSubtitleDownloadService;
            _vocalOnsetService = vocalOnsetService;
            _highAccuracyInitialPassService = highAccuracyInitialPassService;
            _logger = logger;
        }
        #endregion

        #region 上傳音樂
        public IActionResult Upload()
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("Email")))
            {
                return RedirectToAction("Index", "Home");
            }

            // 初始化空集合，避免 Razor 頁面報 NullReferenceException
            var model = new List<TranscriptionSegment>();
            return View(model);
        }
        [HttpPost]
        public async Task<IActionResult> UpdateTranscription([FromBody] TranscriptionUpdateRequest request, CancellationToken cancellationToken)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            if (request == null || request.SongData == null)
            {
                return BadRequest("請提供有效的歌曲資料");
            }

            if (!HasUsableManualSegments(request.Segments))
            {
                return BadRequest("請至少保留一行有效歌詞");
            }

            try
            {
                await EnsureChineseTitleAliasAsync(request.SongData, cancellationToken);
                string songUid = await _songPersistence.AddSongToDatabaseAsync(request.SongData);

                await _songPersistence.CreateDynamicSongTableAsync(songUid);

                await _songPersistence.InsertManualSegmentsAsync(songUid, request.Segments, cancellationToken);
                await _songPersistence.AppendProducerSongAsync(userEmail, songUid, CancellationToken.None);

                await _postProcessService.RunRubyRomanEnrichmentAsync(songUid, cancellationToken);
                return Json(new { success = true, redirectUrl = Url.Action("Manage", "Media") });
            }
            catch (DuplicateYouTubeSongException ex)
            {
                return Conflict(new
                {
                    success = false,
                    error = "此 YouTube 影片已存在",
                    existingSongUid = ex.ExistingSongUid,
                    redirectUrl = Url.Action("Index", "Lyrics", new { songUid = ex.ExistingSongUid })
                });
            }
        }

        private static bool HasUsableManualSegments(IReadOnlyCollection<TranscriptionSegment>? segments)
        {
            return segments != null
                   && segments.Any(segment =>
                       double.IsFinite(segment.Start)
                       && segment.Start >= 0
                       && (!string.IsNullOrWhiteSpace(segment.Text) || !string.IsNullOrWhiteSpace(segment.Chinese)));
        }

        private static bool HasUsableSummonLyrics(IReadOnlyCollection<LyricEntry>? lyrics)
        {
            return lyrics != null
                   && lyrics.Any(lyric =>
                       double.IsFinite(lyric.Time)
                       && lyric.Time >= 0
                       && (!string.IsNullOrWhiteSpace(lyric.Japanese) || !string.IsNullOrWhiteSpace(lyric.Chinese)));
        }
        public class TranscriptionRequest
        {
            public string YoutubeUrl { get; set; } = string.Empty;
            public string Language { get; set; } = string.Empty;
        }
        public class TranscriptionUpdateRequest
        {
            public TranscribeRequest SongData { get; set; } = new();
            public List<TranscriptionSegment> Segments { get; set; } = new();
        }
        #endregion

        #region 主流程 (將Youtube音樂轉換成日文)
        public static string BuildPrecisionCorrectionWarningMessage(string reason, string? detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return $"⚠️ 精準校正未取得完整結果（{reason}）";

            var normalizedDetail = detail.Replace("\r", " ").Replace("\n", " ").Trim();
            normalizedDetail = normalizedDetail.Replace("\"", "'");
            normalizedDetail = Regex.Replace(normalizedDetail, @"\s+", " ");
            const int maxDetailLength = 72;
            if (normalizedDetail.Length > maxDetailLength)
                normalizedDetail = normalizedDetail[..maxDetailLength] + "...";

            return $"⚠️ 精準校正未取得完整結果（{reason}：{normalizedDetail}）";
        }

        private static bool TryApplyCompleteMonotonicAlignments(
            IList<LyricSegment> segments,
            IReadOnlyList<VocalOnsetDetectionService.LyricTimingAlignment> alignments,
            out int matchedCount)
        {
            matchedCount = alignments.Count(alignment => alignment.IsMatched);
            if (segments.Count == 0
                || alignments.Count != segments.Count
                || matchedCount != segments.Count
                || !AreAlignmentTimestampsMonotonic(alignments))
            {
                return false;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].TimeStamp = alignments[i].Start;
            }

            return true;
        }

        private static bool AreAlignmentTimestampsMonotonic(
            IReadOnlyList<VocalOnsetDetectionService.LyricTimingAlignment> alignments)
        {
            const double minimumAutomaticLineGapSeconds = 0.15;
            double? previous = null;
            foreach (var alignment in alignments)
            {
                if (!alignment.IsMatched)
                    continue;

                if (previous.HasValue && alignment.Start - previous.Value < minimumAutomaticLineGapSeconds)
                    return false;

                previous = alignment.Start;
            }

            return true;
        }

        private static bool TryApplySameVideoSubtitleTimeAnchors(
            IList<LyricSegment> segments,
            IReadOnlyList<LyricSegment> subtitleAnchors,
            out int appliedCount)
        {
            appliedCount = 0;
            if (segments.Count == 0)
            {
                return false;
            }

            var usableAnchors = NormalizeSubtitleAnchorCount(subtitleAnchors, segments.Count);
            if (usableAnchors.Count != segments.Count)
            {
                return TryApplyTextAnchoredSubtitleFixedOffset(segments, subtitleAnchors, out appliedCount);
            }

            double previousTimestamp = -1;
            for (var i = 0; i < usableAnchors.Count; i++)
            {
                var timestamp = usableAnchors[i].TimeStamp;
                if (timestamp < previousTimestamp)
                {
                    return false;
                }

                previousTimestamp = timestamp;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].TimeStamp = Math.Round(usableAnchors[i].TimeStamp, 2, MidpointRounding.AwayFromZero);
            }

            appliedCount = segments.Count;
            return true;
        }

        private static bool TryApplyTextAnchoredSubtitleFixedOffset(
            IList<LyricSegment> segments,
            IReadOnlyList<LyricSegment> subtitleAnchors,
            out int evidenceCount)
        {
            evidenceCount = 0;
            if (segments.Count < 4 || subtitleAnchors.Count < 4)
            {
                return false;
            }

            var offsets = new List<double>();
            var nextSubtitleIndex = 0;
            for (var lyricIndex = 0; lyricIndex < segments.Count; lyricIndex++)
            {
                var matchedSubtitleIndex = FindNextSubtitleTextAnchor(
                    segments[lyricIndex].Japanese,
                    subtitleAnchors,
                    nextSubtitleIndex,
                    lyricIndex,
                    segments.Count);
                if (matchedSubtitleIndex < 0)
                {
                    continue;
                }

                var offset = subtitleAnchors[matchedSubtitleIndex].TimeStamp - segments[lyricIndex].TimeStamp;
                if (Math.Abs(offset) <= 120)
                {
                    offsets.Add(offset);
                }

                nextSubtitleIndex = matchedSubtitleIndex + 1;
            }

            evidenceCount = offsets.Count;
            var requiredEvidence = Math.Max(6, Math.Min(18, (int)Math.Ceiling(segments.Count * 0.30)));
            if (offsets.Count < requiredEvidence)
            {
                return false;
            }

            offsets.Sort();
            var median = Median(offsets);
            var deviations = offsets
                .Select(offset => Math.Abs(offset - median))
                .OrderBy(deviation => deviation)
                .ToList();
            var medianAbsoluteDeviation = Median(deviations);
            if (Math.Abs(median) > 60 || medianAbsoluteDeviation > 0.65)
            {
                return false;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].TimeStamp = Math.Round(Math.Max(0, segments[i].TimeStamp + median), 2, MidpointRounding.AwayFromZero);
            }

            return true;
        }

        private static int FindNextSubtitleTextAnchor(
            string? lyricText,
            IReadOnlyList<LyricSegment> subtitleAnchors,
            int nextSubtitleIndex,
            int lyricIndex,
            int lyricCount)
        {
            var windowEnd = Math.Min(subtitleAnchors.Count, nextSubtitleIndex + 8);
            for (var subtitleIndex = nextSubtitleIndex; subtitleIndex < windowEnd; subtitleIndex++)
            {
                if (IsSubtitleTextAnchorMatch(lyricText, subtitleAnchors[subtitleIndex].Japanese))
                {
                    return subtitleIndex;
                }
            }

            var expectedIndex = (int)Math.Round((double)lyricIndex * subtitleAnchors.Count / Math.Max(1, lyricCount));
            var fallbackStart = Math.Max(nextSubtitleIndex, expectedIndex - 4);
            var fallbackEnd = Math.Min(subtitleAnchors.Count, expectedIndex + 8);
            for (var subtitleIndex = fallbackStart; subtitleIndex < fallbackEnd; subtitleIndex++)
            {
                if (IsSubtitleTextAnchorMatch(lyricText, subtitleAnchors[subtitleIndex].Japanese))
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

        private static bool TryApplyStableSubtitleFixedOffset(
            IList<LyricSegment> segments,
            IReadOnlyList<LyricSegment> subtitleAnchors,
            out int evidenceCount)
        {
            evidenceCount = 0;
            var count = Math.Min(segments.Count, subtitleAnchors.Count);
            if (count < 4)
            {
                return false;
            }

            var offsets = Enumerable.Range(0, Math.Min(count, 12))
                .Select(index => subtitleAnchors[index].TimeStamp - segments[index].TimeStamp)
                .OrderBy(offset => offset)
                .ToList();
            var median = Median(offsets);
            var deviations = offsets
                .Select(offset => Math.Abs(offset - median))
                .OrderBy(deviation => deviation)
                .ToList();
            var medianAbsoluteDeviation = Median(deviations);
            if (Math.Abs(median) > 30 || medianAbsoluteDeviation > 0.4)
            {
                return false;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                segments[i].TimeStamp = Math.Round(Math.Max(0, segments[i].TimeStamp + median), 2, MidpointRounding.AwayFromZero);
            }

            evidenceCount = segments.Count;
            return true;
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

        private static IReadOnlyList<LyricSegment> NormalizeSubtitleAnchorCount(
            IReadOnlyList<LyricSegment> subtitleAnchors,
            int lyricCount)
        {
            if (subtitleAnchors.Count == lyricCount)
            {
                return subtitleAnchors;
            }

            if (subtitleAnchors.Count == lyricCount + 1)
            {
                return subtitleAnchors.Take(lyricCount).ToList();
            }

            return subtitleAnchors;
        }

        [HttpPost]
        [Route("Media/webtranscribe")]
        public async Task<IActionResult> WebTranscribe([FromBody] TranscribeRequest request)
        {
            // ── SSE 初始化 ──────────────────────────────────────────────────
            var ct = Response.HttpContext.RequestAborted;
            Response.ContentType = "text/event-stream; charset=utf-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Connection"] = "keep-alive";
            Response.Headers["X-Accel-Buffering"] = "no";   // 停用 nginx 緩衝

            // 停用 ASP.NET Core 回應緩衝，確保逐事件立即推送
            var bodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            bodyFeature?.DisableBuffering();

            async Task SendEvent(string eventName, object data)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var json = JsonSerializer.Serialize(data);
                    await Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                catch (OperationCanceledException) { /* 客戶端已斷線，靜默忽略 */ }
            }

            // ── 前置驗證 ─────────────────────────────────────────────────────
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                await SendEvent("error", new { message = "請先登入" });
                return new EmptyResult();
            }
            if (string.IsNullOrEmpty(request?.YouTubeUrl))
            {
                await SendEvent("error", new { message = "YouTube URL is required." });
                return new EmptyResult();
            }

            try
            {
                // ════════════════════════════════════════════════
                // Step 0：自動取得 YouTube 標題、作者與時長，並限制 6 分鐘以上不可自動轉換
                // ════════════════════════════════════════════════
                await SendEvent("progress", new { message = "🔍 正在從 YouTube 抓取標題與時長..." });
                _logger.LogInformation("Step 0: 交由 YouTube metadata resolver 補齊標題/作者並取得時長");

                var metadataResolution = await _youTubeMetadataResolverService.ResolveAsync(
                    request.YouTubeUrl,
                    request.Title,
                    request.Artist,
                    ct);

                request.Title = metadataResolution.Title;
                request.Artist = metadataResolution.Artist;

                if (metadataResolution.DurationSeconds is > MaxAutomaticTranscriptionDurationSeconds)
                {
                    var durationMinutes = metadataResolution.DurationSeconds.Value / 60.0;
                    _logger.LogInformation(
                        "WebTranscribe: YouTube duration {DurationSeconds:F1}s exceeds automatic transcription limit {LimitSeconds:F1}s for {YouTubeUrl}",
                        metadataResolution.DurationSeconds.Value,
                        MaxAutomaticTranscriptionDurationSeconds,
                        request.YouTubeUrl);
                    await SendEvent("error", new
                    {
                        message = $"這首歌長度約 {durationMinutes:F1} 分鐘，超過 6 分鐘自動轉換上限。請改用自填歌詞／歌曲召喚流程手動上傳。",
                        reason = "duration_limit_exceeded",
                        durationSeconds = metadataResolution.DurationSeconds.Value,
                        maxDurationSeconds = MaxAutomaticTranscriptionDurationSeconds
                    });
                    return new EmptyResult();
                }

                if (!string.IsNullOrWhiteSpace(request.Title))
                {
                    await SendEvent("progress", new { message = string.IsNullOrWhiteSpace(request.Artist) ? $"📋 {request.Title}" : $"📋 {request.Title} - {request.Artist}" });
                }

                // ════════════════════════════════════════════════
                // Step 1：取時間戳
                //   1A. LrcLib synced lyrics（優先）
                //   1B. NetEase 網易雲音樂
                //   1C. Whisper fallback（僅在 1A~1B 全部失敗時）
                //   ※ 不使用 YouTube 自動字幕（ASR 品質差）
                // ════════════════════════════════════════════════
                await SendEvent("progress", new { message = "⏳ 搜尋歌詞時間戳..." });

                List<LyricSegment>? timestampSegments = null;
                bool fromLrcLibOrNetEase = false; // 追蹤歌詞來源，用於判斷是否需要偏移校正
                bool fromTypingTube = false; // TypingTube 是人工時間軸，預設不再用 ASR 精修，避免把好時間軸拉壞

                // 1A：LrcLib synced lyrics（優先）
                {
                    bool hasTitleForLrc = !string.IsNullOrWhiteSpace(request.Title);
                    if (hasTitleForLrc)
                    {
                        await SendEvent("progress", new { message = "🔍 搜尋 LrcLib 精準時間戳..." });
                        _logger.LogInformation("嘗試 LrcLib synced lyrics");

                        try
                        {
                            var lrcResult = await _lrcLibService.FetchSyncedLyricsAsync(
                                request.Title,
                                request.Artist ?? string.Empty,
                                durationSeconds: metadataResolution.DurationSeconds);

                            if (lrcResult != null && lrcResult.Count > 0)
                            {
                                _logger.LogInformation("LrcLib 取得 {Count} 行時間戳歌詞，跳過 Whisper", lrcResult.Count);
                                timestampSegments = lrcResult.Select(r => new LyricSegment
                                {
                                    TimeStamp = r.TimeStamp,
                                    Japanese = r.Japanese,
                                    Chinese = ""
                                }).ToList();
                                fromLrcLibOrNetEase = true; // 標記來源為 LrcLib
                            }
                            else
                            {
                                _logger.LogInformation("LrcLib 未找到 synced lyrics，繼續 Whisper fallback");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "LrcLib FetchSyncedLyricsAsync failed in Step 1B");
                        }
                    }
                }

                // 1B：NetEase 網易雲音樂（LrcLib 失敗時）
                if (timestampSegments == null || !timestampSegments.Any())
                {
                    bool hasTitleForNe = !string.IsNullOrWhiteSpace(request.Title);
                    if (hasTitleForNe)
                    {
                        await SendEvent("progress", new { message = "🎵 搜尋網易雲歌詞..." });
                        _logger.LogInformation("LrcLib 未找到，嘗試 NetEase synced lyrics");

                        try
                        {
                            var neResult = await _netEaseLrcService.FetchLyricsAsync(
                                request.Title, request.Artist ?? string.Empty);

                            if (neResult != null && neResult.Count > 0)
                            {
                                _logger.LogInformation("NetEase 取得 {Count} 行時間戳歌詞，跳過 Whisper", neResult.Count);
                                timestampSegments = neResult.Select(r => new LyricSegment
                                {
                                    TimeStamp = r.TimeStamp,
                                    Japanese = r.Japanese,
                                    Chinese = ""
                                }).ToList();
                                fromLrcLibOrNetEase = true; // 標記來源為 NetEase
                            }
                            else
                            {
                                _logger.LogInformation("NetEase 未找到歌詞，繼續 Whisper fallback");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "NetEaseLrcService.FetchLyricsAsync failed in Step 1C");
                        }
                    }
                }

                // 1C：TypingTube 人工時間軸（LrcLib / NetEase 都失敗時）
                if (timestampSegments == null || !timestampSegments.Any())
                {
                    await SendEvent("progress", new { message = "⌨️ 搜尋 TypingTube 人工時間軸..." });
                    try
                    {
                        var typingTubeResult = await _typingTubeLyricsService.FetchLyricsByYouTubeUrlAsync(
                            request.YouTubeUrl,
                            request.Title,
                            ct);

                        if (typingTubeResult != null && typingTubeResult.Count > 0)
                        {
                            timestampSegments = typingTubeResult;
                            fromTypingTube = true;
                            _logger.LogInformation("TypingTube 取得 {Count} 行人工時間軸", typingTubeResult.Count);
                            await SendEvent("progress", new { message = $"✅ 取得 TypingTube 人工時間軸（{typingTubeResult.Count} 行）" });
                        }
                        else
                        {
                            _logger.LogInformation("TypingTube 未找到可用人工時間軸，繼續 Whisper fallback");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TypingTube fallback failed");
                    }
                }

                bool fromYouTubeAutoCaptionTimeAnchors = false;
                if (timestampSegments == null || !timestampSegments.Any())
                {
                    await SendEvent("progress", new { message = "🔎 搜尋 YouTube 自動字幕時間錨..." });
                    try
                    {
                        var autoCaptionAnchors = await _youTubeSubtitleDownloadService.TryDownloadAutoCaptionTimeAnchorsAsync(
                            request.YouTubeUrl,
                            ct);
                        if (autoCaptionAnchors != null && autoCaptionAnchors.Count > 0)
                        {
                            timestampSegments = autoCaptionAnchors;
                            fromYouTubeAutoCaptionTimeAnchors = true;
                            await SendEvent("progress", new { message = $"✅ 取得 YouTube 時間錨（{autoCaptionAnchors.Count} 行），僅用於對齊正式歌詞" });
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "YouTube auto caption time anchors fallback failed");
                    }
                }

                bool precisionCorrectionCompleted = false;
                string? precisionCorrectionReviewReason = null;
                string? precisionCorrectionCompletedReason = null;

                if (!fromTypingTube && fromLrcLibOrNetEase && timestampSegments != null && timestampSegments.Any())
                {
                    await SendEvent("progress", new { message = "🔎 檢查 YouTube 官方字幕時間軸..." });
                    try
                    {
                        var officialSubtitleAnchors = await _youTubeSubtitleDownloadService.TryDownloadSubtitlesAsync(
                            request.YouTubeUrl,
                            ct);
                        if (officialSubtitleAnchors != null
                            && TryApplySameVideoSubtitleTimeAnchors(timestampSegments, officialSubtitleAnchors, out var appliedAnchorCount))
                        {
                            precisionCorrectionCompleted = true;
                            precisionCorrectionCompletedReason = $"同步歌詞來源已套用同影片 YouTube 官方字幕時間軸，共 {appliedAnchorCount} 行。";
                            _logger.LogInformation(
                                "LrcLib/NetEase 時間戳已改用 YouTube 官方字幕時間軸，Applied={Applied}/{Total}",
                                appliedAnchorCount,
                                timestampSegments.Count);
                            await SendEvent("progress", new { message = $"✅ {precisionCorrectionCompletedReason}" });
                        }
                        else if (officialSubtitleAnchors != null)
                        {
                            _logger.LogInformation(
                                "YouTube 官方字幕行數未吻合，保留 LrcLib/NetEase 時間戳並進入 ASR 校正，SubtitleCount={SubtitleCount}, LyricCount={LyricCount}",
                                officialSubtitleAnchors.Count,
                                timestampSegments.Count);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "YouTube 官方字幕時間軸檢查失敗，改走 ASR 精準校正");
                    }
                }

                // 1B/1C 後處理：LrcLib/NetEase 先提供初始時間戳，
                // 再用整首音訊 word-level timestamps 做逐句精準校正。
                // TypingTube 已是人工打點資料，先不跑 ASR 精修，避免 short meme song 被 ASR 幻覺拉壞。
                if (!precisionCorrectionCompleted && !fromTypingTube && fromLrcLibOrNetEase && timestampSegments != null && timestampSegments.Any())
                {
                    _logger.LogInformation("先使用 LrcLib/NetEase 時間戳，接著嘗試逐句精準校正");
                    await SendEvent("progress", new { message = "✅ 取得同步歌詞時間戳，準備精準校正..." });

                    try
                    {
                        await SendEvent("progress", new { message = "🎯 逐句校正歌詞秒數..." });
                        var precisionAudioPath = await _audioDownloader.DownloadAudioAsync(request.YouTubeUrl, extractAudioAsMp3: true);

                        if (!string.IsNullOrWhiteSpace(precisionAudioPath) && System.IO.File.Exists(precisionAudioPath))
                        {
                            try
                            {
                                var lyricSeeds = timestampSegments
                                    .Select(seg => new VocalOnsetDetectionService.LyricTimingSeed(seg.Japanese ?? string.Empty, seg.TimeStamp))
                                    .ToList();

                                using var alignmentTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                alignmentTimeoutCts.CancelAfter(TimeSpan.FromSeconds(300));

                                var alignmentAttempt = await _vocalOnsetService.AlignLyricsToAudioAsync(
                                    precisionAudioPath,
                                    lyricSeeds,
                                    alignmentTimeoutCts.Token);

                                if (alignmentAttempt.IsSuccess && alignmentAttempt.Alignments.Count == timestampSegments.Count)
                                {
                                    if (TryApplyCompleteMonotonicAlignments(timestampSegments, alignmentAttempt.Alignments, out var matchedCount))
                                    {
                                        precisionCorrectionCompleted = true;
                                        precisionCorrectionCompletedReason = alignmentAttempt.FixedOffsetSeconds.HasValue
                                            ? $"同步歌詞來源經同影片 ASR 驗證，已套用全曲固定偏移 {alignmentAttempt.FixedOffsetSeconds.Value:F2} 秒，共 {timestampSegments.Count} 行。"
                                            : $"同步歌詞來源已完成逐句精準校正，共 {timestampSegments.Count} 行。";
                                        _logger.LogInformation(
                                            "Step 1C+: 精準校正完成，策略={Strategy}, 固定偏移={Offset}, 成功 {Matched}/{Total} 句，Whisper words={WordCount}",
                                            alignmentAttempt.CorrectionStrategy,
                                            alignmentAttempt.FixedOffsetSeconds,
                                            matchedCount,
                                            timestampSegments.Count,
                                            alignmentAttempt.WordCount);
                                        await SendEvent("progress", new { message = $"✅ {precisionCorrectionCompletedReason}" });
                                    }
                                    else
                                    {
                                        precisionCorrectionReviewReason = matchedCount == timestampSegments.Count
                                            ? "逐句精準校正未完成：校正結果時間戳非遞增或過於密集，保留同步歌詞時間戳"
                                            : $"逐句精準校正未完成：僅校正 {matchedCount}/{timestampSegments.Count} 句，保留同步歌詞時間戳";
                                        _logger.LogWarning(
                                            "Step 1C+: 逐句精準校正未套用，Matched={Matched}/{Total}, Monotonic={Monotonic}",
                                            matchedCount,
                                            timestampSegments.Count,
                                            AreAlignmentTimestampsMonotonic(alignmentAttempt.Alignments));
                                        await SendEvent("progress", new { message = $"⚠️ {precisionCorrectionReviewReason}" });
                                    }
                                }
                                else
                                {
                                    var reason = alignmentAttempt.FailureReason ?? "alignment_result_incomplete";
                                    var detail = alignmentAttempt.FailureDetail ?? $"alignments={alignmentAttempt.Alignments.Count}, expected={timestampSegments.Count}";
                                    precisionCorrectionReviewReason = BuildPrecisionCorrectionWarningMessage(reason, detail).TrimStart('⚠', '️', ' ');
                                    _logger.LogWarning("Step 1C+: 精準校正未取得完整結果，reason={Reason}, detail={Detail}", reason, detail);
                                    await SendEvent("progress", new { message = BuildPrecisionCorrectionWarningMessage(reason, detail) });
                                }
                            }
                            finally
                            {
                                if (System.IO.File.Exists(precisionAudioPath))
                                    System.IO.File.Delete(precisionAudioPath);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Step 1C+: precision audio 下載成功但找不到檔案：{Path}", precisionAudioPath);
                            precisionCorrectionReviewReason = "逐句精準校正未完成：下載音訊失敗，保留同步歌詞時間戳";
                            await SendEvent("progress", new { message = "⚠️ 精準校正下載音訊失敗，保留原始同步歌詞時間戳" });
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Step 1C+: 逐句精準校正超過 120 秒，保留原始同步歌詞時間戳");
                        precisionCorrectionReviewReason = "逐句精準校正未完成：校正逾時，保留同步歌詞時間戳";
                        await SendEvent("progress", new { message = "⚠️ 逐句精準校正逾時，保留原始同步歌詞時間戳" });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Step 1C+: 逐句精準校正失敗，保留原始同步歌詞時間戳");
                        precisionCorrectionReviewReason = "逐句精準校正未完成：校正失敗，保留同步歌詞時間戳";
                        await SendEvent("progress", new { message = "⚠️ 逐句精準校正失敗，保留原始同步歌詞時間戳" });
                    }
                }

                // 1C：Whisper fallback（LrcLib、NetEase 全部失敗時）
                if (timestampSegments == null || !timestampSegments.Any())
                {
                    await SendEvent("progress", new { message = "⏳ 改用語音辨識（Whisper）..." });
                    _logger.LogInformation("yt-dlp、LrcLib、NetEase 均失敗，改用 Whisper 語音辨識取時間戳");

                    try
                    {
                        var audioFilePath = await _audioDownloader.DownloadAudioAsync(request.YouTubeUrl, extractAudioAsMp3: true);
                        if (!string.IsNullOrEmpty(audioFilePath))
                        {
                            // 1C-i：ffmpeg 裁掉前奏靜音，減少 Whisper 時間偏移
                            double trimOffset = 0.0;

                            try
                            {
                                var preprocessResult = await _audioPreprocessService.TrimLeadingSilenceAsync(audioFilePath, ct);
                                audioFilePath = preprocessResult.AudioFilePath;
                                trimOffset = preprocessResult.TrimOffsetSeconds;
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ffEx)
                            {
                                _logger.LogWarning(ffEx, "ffmpeg 靜音裁切例外，使用原始音訊");
                            }

                            try
                            {
                                _logger.LogInformation("Step 1D: 呼叫本機 faster-whisper 初始分句 fallback...");
                                timestampSegments = await _vocalOnsetService.TranscribeInitialSegmentsAsync(audioFilePath, ct);
                                _logger.LogInformation("Step 1D: 初始分句得到 {Count} 個 segments",
                                    timestampSegments?.Count ?? 0);

                                // 1C-ii：若有裁切前奏，補回 trimOffset
                                if (trimOffset > 0.0 && timestampSegments != null)
                                {
                                    _logger.LogInformation("套用 trimOffset={Offset:F2}s 到 {Count} 個初始分句時間戳",
                                        trimOffset, timestampSegments.Count);
                                    foreach (var seg in timestampSegments)
                                        seg.TimeStamp += trimOffset;
                                }

                                if (timestampSegments == null || timestampSegments.Count == 0)
                                {
                                    _logger.LogWarning("Step 1D: 本機初始分句未產出可用 segments");
                                }
                            }
                            finally
                            {
                                if (System.IO.File.Exists(audioFilePath))
                                    System.IO.File.Delete(audioFilePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Whisper fallback failed");
                        timestampSegments = null;
                    }
                }

                ct.ThrowIfCancellationRequested();

                // 完全沒有時間戳 → 仍建立空白歌曲，讓使用者手動填
                if (timestampSegments == null || !timestampSegments.Any())
                {
                    _logger.LogWarning("WebTranscribe: 無法取得時間戳，將建立空白歌詞供手動編輯");
                    await SendEvent("progress", new { message = "⚠️ 未取得可用時間戳，將建立空白歌詞供手動編輯" });
                    timestampSegments = new List<LyricSegment>();
                }

                // ════════════════════════════════════════════════
                // Step 1.5：預先取 marumaru 並合併多對一的行
                // 如果 marumaru 一條對應多個 LRC 行，只保留第一行
                // ════════════════════════════════════════════════
                List<LyricSegment>? preAlignedSegments = null;
                bool hasTitleAndArtist = !string.IsNullOrWhiteSpace(request.Title) &&
                                         !string.IsNullOrWhiteSpace(request.Artist);

                if (hasTitleAndArtist && !fromTypingTube)
                {
                    await SendEvent("progress", new { message = "🔍 預先比對 marumaru 歌詞..." });
                    try
                    {
                        preAlignedSegments = await _translationSourceService.TryPreAlignAsync(
                            request.Title ?? string.Empty,
                            request.Artist ?? string.Empty,
                            timestampSegments,
                            ct,
                            preferMarumaruLineCount: !fromLrcLibOrNetEase);

                        if (preAlignedSegments != null && preAlignedSegments.Count > 0)
                        {
                            _logger.LogInformation(
                                "Step 1.5: LRC {LrcCount} 行合併為 {AlignedCount} 行",
                                timestampSegments.Count, preAlignedSegments.Count);

                            if (preAlignedSegments.Count < timestampSegments.Count)
                            {
                                await SendEvent("progress", new
                                {
                                    message = $"✅ 合併 {timestampSegments.Count - preAlignedSegments.Count} 行重複歌詞"
                                });
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Step 1.5: marumaru 預比對失敗，使用原始 timestampSegments");
                    }
                }

                // 若 LrcLib/NetEase 拿到的是錯歌，marumaru 預比對會完全對不到中文。
                // 這時不要繼續把錯歌時間軸寫入 DB；改試 TypingTube（以 YouTube video id 驗證候選頁）。
                if (fromLrcLibOrNetEase
                    && !fromTypingTube
                    && preAlignedSegments is { Count: > 0 }
                    && !preAlignedSegments.Any(seg => !string.IsNullOrWhiteSpace(seg.Chinese)))
                {
                    _logger.LogWarning("Step 1.5: LrcLib/NetEase 與 marumaru 完全無中文匹配，疑似錯歌；改試 TypingTube video-id 時間軸");
                    await SendEvent("progress", new { message = "⚠️ 同步歌詞疑似配錯歌，改試 TypingTube 人工時間軸..." });

                    var typingTubeResult = await _typingTubeLyricsService.FetchLyricsByYouTubeUrlAsync(
                        request.YouTubeUrl,
                        request.Title,
                        ct);

                    if (typingTubeResult != null && typingTubeResult.Count > 0)
                    {
                        timestampSegments = typingTubeResult;
                        fromTypingTube = true;
                        fromLrcLibOrNetEase = false;
                        preAlignedSegments = null;

                        _logger.LogInformation("Step 1.5: 使用 TypingTube 取代疑似錯歌同步來源，共 {Count} 行", typingTubeResult.Count);
                        await SendEvent("progress", new { message = $"✅ 改用 TypingTube 人工時間軸（{typingTubeResult.Count} 行）" });

                        if (hasTitleAndArtist)
                        {
                            preAlignedSegments = await _translationSourceService.TryPreAlignAsync(
                                request.Title ?? string.Empty,
                                request.Artist ?? string.Empty,
                                timestampSegments,
                                ct);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Step 1.5: 疑似錯歌同步來源，但 TypingTube 未找到可替代時間軸；保留原流程結果");
                    }
                }

                if (fromYouTubeAutoCaptionTimeAnchors && preAlignedSegments is not { Count: > 0 })
                {
                    _logger.LogWarning(
                        "Step 1.5: YouTube auto caption anchors were found, but no formal lyrics matched; aborting automatic creation to avoid persisting auto-caption transcription.");
                    await SendEvent("error", new
                    {
                        message = "❌ 未找到可對齊的正式歌詞，已停止自動建立以避免寫入自動字幕聽寫內容。"
                    });
                    return new EmptyResult();
                }

                // 決定要寫入 DB 的 segments
                List<LyricSegment> segmentsToInsert = preAlignedSegments ?? timestampSegments ?? new List<LyricSegment>();
                var stableSegmentsToInsert = segmentsToInsert ?? new List<LyricSegment>();

                if (!fromTypingTube
                    && !fromLrcLibOrNetEase
                    && preAlignedSegments is { Count: > 0 }
                    && stableSegmentsToInsert.Count > 0)
                {
                    await SendEvent("progress", new { message = "🎯 以正式歌詞嘗試逐句對齊音訊..." });
                    try
                    {
                        var precisionAudioPath = await _audioDownloader.DownloadAudioAsync(request.YouTubeUrl, extractAudioAsMp3: true);

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(precisionAudioPath) && System.IO.File.Exists(precisionAudioPath))
                            {
                                var lyricSeeds = stableSegmentsToInsert
                                    .Select(seg => new VocalOnsetDetectionService.LyricTimingSeed(seg.Japanese ?? string.Empty, seg.TimeStamp))
                                    .ToList();

                                using var alignmentTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                alignmentTimeoutCts.CancelAfter(TimeSpan.FromSeconds(300));

                                var alignmentAttempt = await _vocalOnsetService.AlignLyricsToAudioAsync(
                                    precisionAudioPath,
                                    lyricSeeds,
                                    alignmentTimeoutCts.Token);

                                if (alignmentAttempt.IsSuccess && alignmentAttempt.Alignments.Count == stableSegmentsToInsert.Count)
                                {
                                    if (TryApplyCompleteMonotonicAlignments(stableSegmentsToInsert, alignmentAttempt.Alignments, out var matchedCount))
                                    {
                                        precisionCorrectionCompleted = true;
                                        await SendEvent("progress", new { message = $"✅ 正式歌詞逐句對齊完成（{matchedCount}/{stableSegmentsToInsert.Count} 句）" });
                                    }
                                    else
                                    {
                                        precisionCorrectionReviewReason = matchedCount == stableSegmentsToInsert.Count
                                            ? "正式歌詞逐句對齊未完成：校正結果時間戳非遞增或過於密集，保留來源時間錨"
                                            : $"正式歌詞逐句對齊未完成：僅校正 {matchedCount}/{stableSegmentsToInsert.Count} 句，保留來源時間錨";
                                        _logger.LogWarning(
                                            "marumaru fallback 逐句對齊未套用，Matched={Matched}/{Total}, Monotonic={Monotonic}",
                                            matchedCount,
                                            stableSegmentsToInsert.Count,
                                            AreAlignmentTimestampsMonotonic(alignmentAttempt.Alignments));
                                        await SendEvent("progress", new { message = $"⚠️ {precisionCorrectionReviewReason}" });
                                    }
                                }
                                else
                                {
                                    var reason = alignmentAttempt.FailureReason ?? "alignment_result_incomplete";
                                    var detail = alignmentAttempt.FailureDetail ?? $"alignments={alignmentAttempt.Alignments.Count}, expected={stableSegmentsToInsert.Count}";
                                    precisionCorrectionReviewReason = BuildPrecisionCorrectionWarningMessage(reason, detail).TrimStart('⚠', '️', ' ');
                                    await SendEvent("progress", new { message = BuildPrecisionCorrectionWarningMessage(reason, detail) });
                                }
                            }
                        }
                        finally
                        {
                            if (!string.IsNullOrWhiteSpace(precisionAudioPath) && System.IO.File.Exists(precisionAudioPath))
                                System.IO.File.Delete(precisionAudioPath);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        precisionCorrectionReviewReason = "正式歌詞逐句對齊未完成：校正逾時，保留來源時間錨";
                        await SendEvent("progress", new { message = "⚠️ 正式歌詞逐句對齊逾時，保留來源時間錨" });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "marumaru fallback 逐句對齊失敗，保留來源時間錨");
                        precisionCorrectionReviewReason = "正式歌詞逐句對齊未完成：校正失敗，保留來源時間錨";
                        await SendEvent("progress", new { message = "⚠️ 正式歌詞逐句對齊失敗，保留來源時間錨" });
                    }
                }

                // ════════════════════════════════════════════════
                // Step 2：建立資料庫記錄，先以「翻譯中...」填入
                // ════════════════════════════════════════════════
                await SendEvent("progress", new { message = "💾 建立歌曲資料..." });
                _logger.LogInformation("Step 2: 建立資料庫記錄，segmentsToInsert 共 {Count} 行",
                    stableSegmentsToInsert.Count);

                SongPlaceholderCreationResult placeholderPersistenceResult;
                try
                {
                    await EnsureChineseTitleAliasAsync(request, ct);
                    placeholderPersistenceResult = await _songPersistence.CreateSongWithPlaceholdersAsync(request, stableSegmentsToInsert, ct);
                    _logger.LogInformation("Step 2: 歌曲與 placeholder 歌詞建立成功，SongUid={SongUid}, Lyrics={Count}",
                        placeholderPersistenceResult.SongUid,
                        placeholderPersistenceResult.LyricIds.Count);
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Step 2: 資料庫操作失敗");
                    await SendEvent("error", new { message = $"❌ 資料庫錯誤: {dbEx.Message}" });
                    throw;
                }

                string songUid = placeholderPersistenceResult.SongUid;
                var lyricIds = placeholderPersistenceResult.LyricIds;
                await _songPersistence.AppendProducerSongAsync(userEmail, songUid, CancellationToken.None);

                // ── 立即推送 segments，讓使用者看到歌詞 ──
                var placeholderSegments = stableSegmentsToInsert.Select((seg, idx) => new
                {
                    id = idx,
                    seek = 0,
                    start = seg.TimeStamp,
                    end = seg.TimeStamp,
                    text = seg.Japanese,
                    chinese = !string.IsNullOrWhiteSpace(seg.Chinese) ? seg.Chinese : "翻譯中...",
                    songUid
                }).ToList();

                await SendEvent("segments", placeholderSegments);

                ct.ThrowIfCancellationRequested();

                // ════════════════════════════════════════════════
                // Step 3：背景取翻譯（marumaru → 巴哈姆特 → GPT 批次）
                // 如果 Step 1.5 已經從 marumaru 取得翻譯，直接使用
                // ════════════════════════════════════════════════
                List<LyricSegment>? finalSegments = null;

                // 由 translation source workflow service 決定是否可直接沿用 Step 1.5 的翻譯

                // 3A / 3B / 3C：交由 translation source workflow service 決定翻譯來源與 fallback
                if (finalSegments == null)
                {
                    var translationResolution = await _translationSourceService.ResolveFinalSegmentsAsync(
                        request.Title ?? string.Empty,
                        request.Artist ?? string.Empty,
                        stableSegmentsToInsert,
                        preAlignedSegments,
                        ct);
                    finalSegments = translationResolution.Segments;

                    if (translationResolution.Source is TranslationSourceKind.PreAligned or TranslationSourceKind.Marumaru)
                    {
                        await SendEvent("progress", new { message = "✅ 使用 marumaru 翻譯" });
                    }
                }

                ct.ThrowIfCancellationRequested();

                // ════════════════════════════════════════════════
                // Step 4：用實際翻譯更新資料庫，再推送 translations 事件
                // ════════════════════════════════════════════════
                await SendEvent("progress", new { message = "✅ 歌詞已載入！翻譯寫入中..." });

                lyricIds = await _songPersistence.UpdateSongTranslationsAsync(songUid, finalSegments, lyricIds, ct);

                // 推送翻譯更新給前端（前端更新表格中文欄位）
                var translationUpdates = finalSegments.Select((seg, idx) => new
                {
                    id = idx,
                    chinese = seg.Chinese
                }).ToList();
                await SendEvent("translations", translationUpdates);

                var needsCodexTranslation = !HasCompleteChineseTranslations(finalSegments);

                // ════════════════════════════════════════════════
                // Step 5：補 Ruby 注音 + 羅馬拼音（用獨立 Task，不受 SSE 連線取消影響）
                // ════════════════════════════════════════════════
                // 注意：先標記背景狀態，再推 done，避免前端在 done 後立刻斷線導致狀態更新被取消
                var shouldRunBackgroundHighAccuracyPass = false;
                if (needsCodexTranslation)
                {
                    var timingPendingReason = fromLrcLibOrNetEase && !precisionCorrectionCompleted
                        ? $"timing_validation_pending: {precisionCorrectionReviewReason ?? "同步歌詞秒數尚未通過同影片校正"}；"
                        : string.Empty;
                    await _songPersistence.UpdateHighAccuracyStatusAsync(
                        songUid,
                        "translation_pending_codex",
                        $"{timingPendingReason}翻譯未完成：已排入後台補件，完成驗證後會依秒數校正狀態更新。",
                        CancellationToken.None);
                    await SendEvent("progress", new { message = "📝 中文翻譯待後台補齊，歌曲已建立。" });
                }
                else if (fromTypingTube)
                {
                    await _songPersistence.UpdateHighAccuracyStatusAsync(
                        songUid,
                        "high_accuracy_completed",
                        $"TypingTube 人工時間軸匯入，共 {stableSegmentsToInsert.Count} 行；未使用 YouTube 自動字幕。",
                        CancellationToken.None);
                }
                else if (fromLrcLibOrNetEase)
                {
                    if (precisionCorrectionCompleted)
                    {
                        await _songPersistence.UpdateHighAccuracyStatusAsync(
                            songUid,
                            "high_accuracy_completed",
                            precisionCorrectionCompletedReason ?? $"同步歌詞來源已完成同影片精準校正，共 {stableSegmentsToInsert.Count} 行。",
                            CancellationToken.None);
                    }
                    else
                    {
                        await _songPersistence.UpdateHighAccuracyStatusAsync(
                            songUid,
                            "high_accuracy_pending",
                            $"timing_validation_pending: {precisionCorrectionReviewReason ?? "逐句精準校正未完整命中"}；已保留同步歌詞來源時間戳，共 {stableSegmentsToInsert.Count} 行，背景高精度補跑中。",
                            CancellationToken.None);
                        shouldRunBackgroundHighAccuracyPass = true;
                    }
                }
                else if (preAlignedSegments is { Count: > 0 })
                {
                    if (precisionCorrectionCompleted || fromYouTubeAutoCaptionTimeAnchors)
                    {
                        await _songPersistence.UpdateHighAccuracyStatusAsync(
                            songUid,
                            "high_accuracy_completed",
                            precisionCorrectionCompleted
                                ? $"marumaru 正式歌詞已完成逐句精準校正，共 {stableSegmentsToInsert.Count} 行。"
                                : $"marumaru 正式歌詞已套用 YouTube 時間錨，共 {stableSegmentsToInsert.Count} 行。",
                            CancellationToken.None);
                    }
                    else
                    {
                        await _songPersistence.UpdateHighAccuracyStatusAsync(
                            songUid,
                            "high_accuracy_pending",
                            $"{precisionCorrectionReviewReason ?? "正式歌詞逐句對齊未完整命中"}；已先套用 marumaru 正式歌詞，共 {stableSegmentsToInsert.Count} 行，背景高精度補跑中。",
                            CancellationToken.None);
                        shouldRunBackgroundHighAccuracyPass = true;
                    }
                }
                else
                {
                    await _songPersistence.UpdateHighAccuracyStatusAsync(songUid, "high_accuracy_pending", null, CancellationToken.None);
                    shouldRunBackgroundHighAccuracyPass = true;
                }

                await SendEvent("done", new { songUid, redirectUrl = Url.Action("Manage", "Media") });
                _postProcessService.EnqueueRubyRomanEnrichment(songUid);
                if (shouldRunBackgroundHighAccuracyPass)
                {
                    _highAccuracyInitialPassService.EnqueueHighAccuracyInitialPass(songUid);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("WebTranscribe: 客戶端斷線，SSE 已取消");
            }
            catch (DuplicateYouTubeSongException ex)
            {
                _logger.LogInformation(
                    "WebTranscribe: duplicate YouTube video {VideoId}, existing SongUid={SongUid}",
                    ex.VideoId,
                    ex.ExistingSongUid);
                await SendEvent("error", new
                {
                    message = "此 YouTube 影片已存在，請直接使用既有歌曲。",
                    reason = "duplicate_youtube_video",
                    existingSongUid = ex.ExistingSongUid,
                    redirectUrl = Url.Action("Index", "Lyrics", new { songUid = ex.ExistingSongUid })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebTranscribe unhandled exception: {Message}\n{StackTrace}",
                    ex.Message, ex.StackTrace);
                try
                {
                    await SendEvent("error", new { message = "❌ 歌曲自動建立失敗，請稍後再試或通知管理員查看伺服器紀錄。" });
                }
                catch { /* 寫入失敗時靜默忽略 */ }
            }
            return new EmptyResult();
        }

        #endregion

        #region 音樂管理 (包含主管理與協同管理)
        public async Task<IActionResult> Manage()
        {
            string? userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = await _manageQueryService.GetManageViewModelAsync(userEmail, HttpContext.RequestAborted);
            return View(viewModel);
        }

        public async Task<IActionResult> ReviewQueue()
        {
            string? userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = await _manageQueryService.GetManageViewModelAsync(userEmail, HttpContext.RequestAborted);
            viewModel.ProducerSongs = viewModel.ProducerSongs.Where(NeedsTimingReview).ToList();
            viewModel.CollaborationSongs = viewModel.CollaborationSongs.Where(NeedsTimingReview).ToList();
            return View(viewModel);
        }

        private static bool NeedsTimingReview(Songs song)
        {
            return song.HighAccuracyStatus is "high_accuracy_partial" or "high_accuracy_needs_review" or "high_accuracy_failed";
        }

        [HttpPost]
        public async Task<IActionResult> RetryHighAccuracyQueue([FromBody] RetryHighAccuracyQueueRequest? request)
        {
            string? userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            var requestedSongUids = request?.SongUids?
                .Where(songUid => !string.IsNullOrWhiteSpace(songUid))
                .Select(songUid => songUid.Trim())
                .ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);

            List<Songs> managedSongs;
            try
            {
                managedSongs = request?.IncludeAll == true
                    ? await GetAllRetryableHighAccuracySongsForMaintenanceAsync(userEmail, HttpContext.RequestAborted)
                    : await GetManagedSongsAsync(userEmail, HttpContext.RequestAborted);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }

            var queuedSongs = managedSongs
                .Where(song => requestedSongUids.Count == 0 || requestedSongUids.Contains(song.SongUid))
                .Where(IsRetryableHighAccuracyStatus)
                .ToList();

            foreach (var song in queuedSongs)
            {
                await _songPersistence.UpdateHighAccuracyStatusAsync(
                    song.SongUid,
                    "high_accuracy_pending",
                    "已重新排入高精度背景補跑。",
                    CancellationToken.None);
                _highAccuracyInitialPassService.EnqueueHighAccuracyInitialPass(song.SongUid);
            }

            return Ok(new
            {
                success = true,
                queued = queuedSongs.Count,
                songUids = queuedSongs.Select(song => song.SongUid).ToList()
            });
        }

        private static bool IsRetryableHighAccuracyStatus(Songs song)
        {
            return song.HighAccuracyStatus is
                "high_accuracy_partial" or
                "high_accuracy_pending" or
                "high_accuracy_processing" or
                "high_accuracy_needs_review" or
                "high_accuracy_failed";
        }

        public class RetryHighAccuracyQueueRequest
        {
            public List<string>? SongUids { get; set; }
            public bool IncludeAll { get; set; }
        }

        private async Task<List<Songs>> GetManagedSongsAsync(string userEmail, CancellationToken cancellationToken)
        {
            var viewModel = await _manageQueryService.GetManageViewModelAsync(userEmail, cancellationToken);
            return viewModel.ProducerSongs
                .Concat(viewModel.CollaborationSongs)
                .Where(song => !string.IsNullOrWhiteSpace(song.SongUid))
                .GroupBy(song => song.SongUid, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private async Task<List<Songs>> GetAllRetryableHighAccuracySongsForMaintenanceAsync(string userEmail, CancellationToken cancellationToken)
        {
            var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
            var maintenanceEmail = configuration?["TestAccount:Email"];
            if (string.IsNullOrWhiteSpace(maintenanceEmail)
                || !string.Equals(userEmail, maintenanceEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Only maintenance account can retry all high accuracy songs.");
            }

            return await _lyricsQueryService.GetRetryableHighAccuracySongsAsync(cancellationToken);
        }
        #endregion

        #region 歌曲編輯 (含協作者管理)
        [HttpGet("Edit/{songUid}")]
        public async Task<IActionResult> Edit(string songUid)
        {
            if (string.IsNullOrEmpty(songUid))
            {
                return RedirectToAction("Index", "Home");
            }

            string? userEmail = HttpContext.Session.GetString("Email");

            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = await _editQueryService.GetEditSongViewModelAsync(userEmail, songUid, HttpContext.RequestAborted);
            if (viewModel == null)
            {
                return RedirectToAction("Index", "Home");
            }

            return View(viewModel);
        }

        [HttpPost("Edit/{songUid}")]
        public async Task<IActionResult> Edit(string songUid, EditSongViewModel model)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            if (model.Song == null)
            {
                return BadRequest("Invalid song data.");
            }

            var editableModel = await _editQueryService.GetEditSongViewModelAsync(userEmail, songUid, HttpContext.RequestAborted);
            if (editableModel == null)
            {
                return Forbid();
            }

            await _editMutationService.UpdateSongAndCollaboratorsAsync(songUid, model, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "歌曲已成功更新！";

            return RedirectToAction("Manage");
        }
        #endregion

        #region 歌詞編輯
        /// <summary>
        /// 歌詞編輯
        /// </summary>
        /// <param name="songUid"></param>
        /// <returns></returns>
        [HttpGet("EditLyrics/{songUid}")]
        public async Task<IActionResult> EditLyrics(string songUid)
        {
            if (string.IsNullOrEmpty(songUid))
            {
                return RedirectToAction("Index", "Home");
            }

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            var viewModel = await _lyricsQueryService.GetEditLyricsViewModelAsync(userEmail, songUid, HttpContext.RequestAborted);
            if (viewModel == null)
            {
                return RedirectToAction("Index", "Home");
            }

            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> EditLyrics([FromBody] EditLyricsRequest request)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            if (request == null || request.Lyrics == null || string.IsNullOrEmpty(request.SongUid))
            {
                return BadRequest("Invalid data.");
            }

            if (!await CanEditLyricsAsync(userEmail, request.SongUid, HttpContext.RequestAborted))
            {
                return Forbid();
            }

            await _lyricsMutationService.UpdateLyricsAsync(request.SongUid, request.Lyrics, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "歌詞已成功更新！";

            return Json(new { success = true, redirectUrl = Url.Action("Manage") });
        }
        public class EditLyricsRequest
        {
            public string SongUid { get; set; } = string.Empty;
            public string MNmae { get; set; } = string.Empty;
            public List<LyricSegment> Lyrics { get; set; } = new();
        }

        [HttpPost]
        public async Task<IActionResult> UpdateOrder([FromBody] UpdateOrderRequest request)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            if (request == null || string.IsNullOrEmpty(request.SongUid) || request.NewOrder == null || request.NewOrder.Count == 0)
            {
                return BadRequest("Invalid order data.");
            }

            if (!await CanEditLyricsAsync(userEmail, request.SongUid, HttpContext.RequestAborted))
            {
                return Forbid();
            }

            try
            {
                await _lyricsMutationService.UpdateOrderAsync(request.SongUid, request.NewOrder, HttpContext.RequestAborted);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }

            return Ok(new { success = true });
        }

        public class UpdateOrderRequest
        {
            public string SongUid { get; set; } = string.Empty;
            public List<int> NewOrder { get; set; } = new();
        }
        #endregion

        #region 歌詞刪除
        [HttpPost]
        public async Task<IActionResult> DeleteLyric([FromBody] DeleteLyricRequest request)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            if (request == null || string.IsNullOrWhiteSpace(request.songUid))
            {
                return Json(new { success = false, message = "Invalid songUid" });
            }

            if (!await CanEditLyricsAsync(userEmail, request.songUid, HttpContext.RequestAborted))
            {
                return Forbid();
            }

            try
            {
                bool deleted = await _lyricsMutationService.DeleteLyricAsync(request.songUid, request.lyricId, HttpContext.RequestAborted);

                if (deleted)
                {
                    return Json(new { success = true });
                }

                return Json(new { success = false, message = "No records deleted. The LyricID may not exist." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        public class DeleteLyricRequest
        {
            public int lyricId { get; set; }
            public string songUid { get; set; } = string.Empty;
        }
        private Task EnsureChineseTitleAliasAsync(TranscribeRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.ChineseTitleAlias) && !string.IsNullOrWhiteSpace(request.Title))
            {
                _logger.LogInformation("未填中文歌名，略過 runtime OpenAI 自動翻譯以避免額外 API 費用。Title={Title}", request.Title);
            }

            return Task.CompletedTask;
        }

        private Task EnsureChineseTitleAliasAsync(SummonRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.ChineseTitleAlias) && !string.IsNullOrWhiteSpace(request.SongTitle))
            {
                _logger.LogInformation("未填中文歌名，略過 runtime OpenAI 自動翻譯以避免額外 API 費用。Title={Title}", request.SongTitle);
            }

            return Task.CompletedTask;
        }

        private static bool HasCompleteChineseTranslations(IReadOnlyList<LyricSegment> segments)
            => segments.Count > 0 && segments.All(segment => HasUsableChineseTranslation(segment.Chinese));

        private static bool HasUsableChineseTranslation(string? chinese)
            => !string.IsNullOrWhiteSpace(chinese)
                && !string.Equals(chinese.Trim(), "翻譯中...", StringComparison.Ordinal);

        private async Task<bool> CanEditLyricsAsync(string userEmail, string songUid, CancellationToken cancellationToken)
            => await _lyricsQueryService.GetEditLyricsViewModelAsync(userEmail, songUid, cancellationToken) != null;

        #endregion

        #region 歌曲召喚
        public IActionResult Summon()
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("Email")))
            {
                return RedirectToAction("Index", "Home");
            }

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Summon([FromBody] SummonRequest request)
        {
            if (request == null)
            {
                return BadRequest("請提供有效的歌曲資訊");
            }

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Index", "Home");
            }

            if (!HasUsableSummonLyrics(request.Lyrics))
            {
                return BadRequest("請至少保留一行有效歌詞並完成時間標記");
            }

            try
            {
                var preparedLyrics = await _summonPreparationService.PrepareLyricsAsync(request.Lyrics, HttpContext.RequestAborted);
                await EnsureChineseTitleAliasAsync(request, HttpContext.RequestAborted);
                string songUid = await _songPersistence.CreateSummonedSongAsync(request, preparedLyrics, HttpContext.RequestAborted);
                await _songPersistence.AppendProducerSongAsync(userEmail, songUid, HttpContext.RequestAborted);

                return Ok(new { Message = "歌曲召喚成功", SongUid = songUid });
            }
            catch (Exception ex)
            {
                if (ex is DuplicateYouTubeSongException duplicate)
                {
                    return Conflict(new
                    {
                        Message = "此 YouTube 影片已存在",
                        ExistingSongUid = duplicate.ExistingSongUid,
                        RedirectUrl = Url.Action("Index", "Lyrics", new { songUid = duplicate.ExistingSongUid })
                    });
                }

                return StatusCode(500, $"錯誤: {ex.Message}");
            }
        }
        #endregion

        #region 歌曲許願留言板
        public IActionResult Wish()
        {
            return View();
        }
        #endregion
    }
}
