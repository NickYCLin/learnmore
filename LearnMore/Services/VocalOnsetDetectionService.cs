using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services
{
    /// <summary>
    /// 透過 Whisper word-level timestamps 與 LRC 歌詞比對，計算時間戳偏移量。
    /// 
    /// 流程：
    /// 1. Whisper 辨識 YouTube 前 60 秒（啟用 word-level timestamps）
    /// 2. 比對 LRC 前 3 句歌詞在 Whisper words 中出現的時間
    /// 3. 計算平均偏移量（排除異常值）
    /// </summary>
    public class VocalOnsetDetectionService
    {
        private static readonly string SecondaryAlignmentTracePath = Path.Combine(
            Path.GetTempPath(),
            "learnmore-second-alignment-trace.log");
        private static readonly string PrecisionAlignmentTracePath = Path.Combine(
            Path.GetTempPath(),
            "learnmore-precision-alignment-trace.log");
        public record LyricTimingSeed(string Text, double ExpectedStart);
        public record WhisperWordTiming(string Word, double Start, double End);
        public record LyricTimingAlignment(string Text, double ExpectedStart, double Start, double End, double Score, bool IsMatched, int StartWordIndex, int EndWordIndex);
        public record AlignmentAttemptResult(
            bool IsSuccess,
            List<LyricTimingAlignment> Alignments,
            string? FailureReason = null,
            string? FailureDetail = null,
            int WordCount = 0,
            int MatchedCount = 0,
            string? CorrectionStrategy = null,
            double? FixedOffsetSeconds = null);
        public record InitialSegmentAttemptResult(List<LyricSegment> Segments, string? FailureReason = null, string? FailureDetail = null);
        private record TranscriptionAttemptResult(WhisperResult? Result, string? FailureReason = null, string? FailureDetail = null);
        public record PhoneticToken(string Surface, string Reading);
        private sealed record StableFixedOffsetResult(double OffsetSeconds, double MedianAbsoluteDeviation, int EvidenceCount, List<LyricTimingAlignment> Alignments);
        private sealed record ComparableWhisperWord(WhisperWord Source, string ComparableText);

        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly VocalOnsetDetectionOptions _options;
        private readonly JapaneseRubyGeneratorService _rubyGenerator;
        private readonly ILogger<VocalOnsetDetectionService> _logger;

        private const int MAX_ANALYSIS_SECONDS = 60;       // 分析前 60 秒
        private const double MIN_OFFSET_THRESHOLD = 0.5;   // 偏移門檻 0.5 秒
        private const double SIMILARITY_THRESHOLD = 0.5;   // 相似度門檻 50%
        private const int MAX_LINES_TO_MATCH = 3;          // 比對前 3 句
        private const int MIN_WORD_LENGTH = 2;             // 最短字詞長度（過濾雜訊）
        private const double LINE_MATCH_THRESHOLD = 0.72;  // 逐句對齊最小相似度
        private const int MAX_WINDOW_WORDS = 64;           // 單句最多嘗試 64 個 words（faster-whisper 日文常切成單字）
        private const int MAX_EXTRA_CHARS = 8;             // 比目標多 8 字就停止擴窗
        private const double START_FALLBACK_THRESHOLD = 0.74; // 整句難以命中時，允許用高信心前半句鎖定 start
        private const double START_FALLBACK_MIN_COVERAGE = 0.65;
        private const double HIGH_COVERAGE_RESCUE_THRESHOLD = 0.55; // 重 ASR drift 但高 coverage + 近時間時，容許保守收斂
        private const double HIGH_COVERAGE_RESCUE_MIN_COVERAGE = 0.92;
        private const double HIGH_COVERAGE_RESCUE_MAX_TIME_DISTANCE = 1.25;
        private const double INITIAL_SEGMENT_SPLIT_GAP_SECONDS = 0.85;
        private const double INITIAL_SEGMENT_TERMINAL_PAUSE_SECONDS = 0.28;
        private const double EARLY_TOKEN_CLAMP_MIN_DURATION = 2.0;
        private const double LOW_SCORE_START_CLAMP_MAX_LEAD = 1.25;
        private static readonly string[] WindowsPythonCandidates =
        {
            @"C:\Python313\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
            @"C:\Windows\py.exe",
            "python"
        };

        public VocalOnsetDetectionService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IOptions<VocalOnsetDetectionOptions> options,
            JapaneseRubyGeneratorService rubyGenerator,
            ILogger<VocalOnsetDetectionService> logger)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _rubyGenerator = rubyGenerator;
            _logger = logger;
        }

        /// <summary>
        /// 透過 word-level 歌詞比對計算偏移量（比對前 3 句）。
        /// </summary>
        public async Task<double> CalculateOffsetByLyricsAsync(
            string audioFilePath,
            List<(double timestamp, string lyrics)> lrcLines,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
            {
                _logger.LogWarning("CalculateOffsetByLyrics: 音訊檔案不存在");
                return 0.0;
            }

            if (lrcLines == null || lrcLines.Count == 0)
            {
                _logger.LogWarning("CalculateOffsetByLyrics: LRC 歌詞為空");
                return 0.0;
            }

            string? trimmedAudioPath = null;

            try
            {
                // Step 1: 擷取前 60 秒
                trimmedAudioPath = await TrimAudioAsync(audioFilePath, 0, MAX_ANALYSIS_SECONDS, cancellationToken);
                if (string.IsNullOrEmpty(trimmedAudioPath))
                {
                    _logger.LogWarning("CalculateOffsetByLyrics: ffmpeg 擷取失敗");
                    return 0.0;
                }

                // Step 2: Whisper 辨識（word-level）
                var transcriptionAttempt = await TranscribeWithWordTimestampsAsync(trimmedAudioPath, cancellationToken);
                var whisperResult = transcriptionAttempt.Result;
                if (whisperResult == null || whisperResult.Words.Count == 0)
                {
                    _logger.LogInformation(
                        "CalculateOffsetByLyrics: Whisper 未辨識到 words，reason={Reason}, detail={Detail}",
                        transcriptionAttempt.FailureReason,
                        transcriptionAttempt.FailureDetail);
                    return 0.0;
                }

                _logger.LogInformation("CalculateOffsetByLyrics: Whisper 辨識到 {Count} 個 words", whisperResult.Words.Count);

                // Step 3: 比對前 N 句，收集偏移量
                var offsets = new List<double>();
                var linesToMatch = lrcLines.Take(MAX_LINES_TO_MATCH).ToList();

                foreach (var (lrcTimestamp, lrcLyrics) in linesToMatch)
                {
                    if (string.IsNullOrWhiteSpace(lrcLyrics)) continue;

                    var matchResult = FindBestWordMatch(whisperResult.Words, lrcLyrics, lrcTimestamp);
                    if (matchResult.HasValue)
                    {
                        var offset = matchResult.Value.whisperTime - lrcTimestamp;
                        offsets.Add(offset);
                        _logger.LogInformation(
                            "  ✓ LRC「{Lrc}」@ {LrcT:F2}s → Whisper「{W}」@ {WT:F2}s，偏移 {O:F2}s",
                            lrcLyrics.Substring(0, Math.Min(15, lrcLyrics.Length)),
                            lrcTimestamp,
                            matchResult.Value.matchedWord,
                            matchResult.Value.whisperTime,
                            offset);
                    }
                    else
                    {
                        _logger.LogInformation("  ✗ LRC「{Lrc}」@ {LrcT:F2}s 未找到匹配",
                            lrcLyrics.Substring(0, Math.Min(15, lrcLyrics.Length)), lrcTimestamp);
                    }
                }

                if (offsets.Count == 0)
                {
                    _logger.LogInformation("CalculateOffsetByLyrics: 沒有任何歌詞匹配成功");
                    LogWhisperWords(whisperResult.Words);
                    return 0.0;
                }

                // Step 4: 計算平均偏移量（排除異常值）
                var finalOffset = CalculateRobustAverage(offsets);

                // 合理性檢查
                if (Math.Abs(finalOffset) > 120)
                {
                    _logger.LogWarning("CalculateOffsetByLyrics: 偏移量 {Offset:F2}s 超過合理範圍，忽略", finalOffset);
                    return 0.0;
                }

                if (Math.Abs(finalOffset) < MIN_OFFSET_THRESHOLD)
                {
                    _logger.LogInformation("CalculateOffsetByLyrics: 偏移量 {Offset:F2}s 低於門檻，不調整", finalOffset);
                    return 0.0;
                }

                _logger.LogInformation("CalculateOffsetByLyrics: 最終偏移量 = {Offset:F2}s（基於 {Count} 個匹配）",
                    finalOffset, offsets.Count);
                return finalOffset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CalculateOffsetByLyrics: 處理失敗");
                return 0.0;
            }
            finally
            {
                CleanupFile(trimmedAudioPath);
            }
        }

        public async Task<List<LyricSegment>> TranscribeInitialSegmentsAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
            => (await TranscribeInitialSegmentsWithDiagnosticsAsync(audioFilePath, localModelOverride: null, allowOpenAiFallback: true, cancellationToken)).Segments;

        public virtual async Task<List<LyricSegment>> TranscribeInitialSegmentsAsync(
            string audioFilePath,
            string? localModelOverride,
            bool allowOpenAiFallback,
            CancellationToken cancellationToken = default)
            => (await TranscribeInitialSegmentsWithDiagnosticsAsync(audioFilePath, localModelOverride, allowOpenAiFallback, cancellationToken)).Segments;

        public virtual async Task<InitialSegmentAttemptResult> TranscribeInitialSegmentsWithDiagnosticsAsync(
            string audioFilePath,
            string? localModelOverride,
            bool allowOpenAiFallback,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            {
                _logger.LogWarning("TranscribeInitialSegmentsAsync: 音訊檔案不存在");
                return new InitialSegmentAttemptResult(new List<LyricSegment>(), "audio_file_missing", audioFilePath);
            }

            var transcriptionAttempt = await TranscribeWithWordTimestampsAsync(audioFilePath, cancellationToken, localModelOverride, allowOpenAiFallback);
            var words = transcriptionAttempt.Result?.Words;
            if (words == null || words.Count == 0)
            {
                _logger.LogWarning(
                    "TranscribeInitialSegmentsAsync: 無可用 word timings，reason={Reason}, detail={Detail}",
                    transcriptionAttempt.FailureReason,
                    transcriptionAttempt.FailureDetail);
                return new InitialSegmentAttemptResult(new List<LyricSegment>(), transcriptionAttempt.FailureReason, transcriptionAttempt.FailureDetail);
            }

            var segments = BuildInitialSegmentsFromWordTimings(words
                .Select(word => new WhisperWordTiming(word.Word, word.Start, word.End))
                .ToList());

            if (segments.Count == 0)
            {
                return new InitialSegmentAttemptResult(new List<LyricSegment>(), "initial_segment_build_empty", $"words={words.Count}");
            }

            return new InitialSegmentAttemptResult(segments);
        }

        public static List<LyricSegment> BuildInitialSegmentsFromWordTimings(IReadOnlyList<WhisperWordTiming> words)
        {
            var segments = new List<LyricSegment>();
            if (words == null || words.Count == 0)
                return segments;

            var currentTokens = new List<WhisperWordTiming>();
            WhisperWordTiming? previousWord = null;

            void FlushCurrentSegment()
            {
                if (currentTokens.Count == 0)
                    return;

                var trimmedTokens = TrimInitialSegmentBoundaryNoiseTokens(currentTokens);
                if (trimmedTokens.Count == 0)
                {
                    currentTokens.Clear();
                    return;
                }

                var normalizedText = NormalizeInitialSegmentText(string.Concat(trimmedTokens.Select(token => token.Word)));
                if (ShouldKeepInitialSegment(normalizedText))
                {
                    segments.Add(new LyricSegment
                    {
                        TimeStamp = trimmedTokens[0].Start,
                        Japanese = normalizedText
                    });
                }

                currentTokens.Clear();
            }

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word.Word))
                    continue;

                var normalizedWord = NormalizeInitialWordToken(word.Word);
                if (IsInitialSegmentationNoise(normalizedWord))
                    continue;

                var currentWord = word with { Word = normalizedWord };
                if (previousWord != null)
                {
                    var gap = Math.Max(0, currentWord.Start - previousWord.End);
                    if (gap >= INITIAL_SEGMENT_SPLIT_GAP_SECONDS ||
                        (EndsWithTerminalPunctuation(previousWord.Word) && gap >= INITIAL_SEGMENT_TERMINAL_PAUSE_SECONDS))
                    {
                        FlushCurrentSegment();
                    }
                }

                currentTokens.Add(currentWord);
                previousWord = currentWord;
            }

            FlushCurrentSegment();
            if (ShouldTreatInitialFallbackAsEmpty(segments))
                return new List<LyricSegment>();

            return segments;
        }

        /// <summary>
        /// 用整首音訊的 word-level timestamps，將逐句歌詞重新對齊到更精確的起訖秒數。
        /// 失敗時回傳 null，呼叫端保留原始時間戳即可。
        /// </summary>
        public Task<AlignmentAttemptResult> AlignLyricsToAudioAsync(
            string audioFilePath,
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            CancellationToken cancellationToken = default)
            => AlignLyricsToAudioAsync(
                audioFilePath,
                lyricSeeds,
                secondaryAlignmentAudioFilePath: null,
                cancellationToken);

        public virtual async Task<AlignmentAttemptResult> AlignLyricsToAudioAsync(
            string audioFilePath,
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            string? secondaryAlignmentAudioFilePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            {
                _logger.LogWarning("AlignLyricsToAudioAsync: 音訊檔案不存在");
                return new AlignmentAttemptResult(false, new List<LyricTimingAlignment>(), "audio_file_missing", audioFilePath);
            }

            if (lyricSeeds == null || lyricSeeds.Count == 0)
            {
                _logger.LogWarning("AlignLyricsToAudioAsync: lyricSeeds 為空");
                return new AlignmentAttemptResult(false, new List<LyricTimingAlignment>(), "lyric_seeds_empty");
            }

            var transcriptionAttempt = await TranscribeWithWordTimestampsAsync(
                audioFilePath,
                cancellationToken,
                allowOpenAiFallback: false);
            var whisperResult = transcriptionAttempt.Result;
            if (whisperResult == null)
            {
                _logger.LogWarning("AlignLyricsToAudioAsync: Whisper 結果為空，reason={Reason}, detail={Detail}", transcriptionAttempt.FailureReason, transcriptionAttempt.FailureDetail);
                return new AlignmentAttemptResult(false, new List<LyricTimingAlignment>(), transcriptionAttempt.FailureReason ?? "whisper_result_null", transcriptionAttempt.FailureDetail);
            }

            if (whisperResult.Words.Count == 0)
            {
                _logger.LogWarning("AlignLyricsToAudioAsync: Whisper 未回傳可用 words，reason={Reason}, detail={Detail}", transcriptionAttempt.FailureReason, transcriptionAttempt.FailureDetail);
                return new AlignmentAttemptResult(false, new List<LyricTimingAlignment>(), transcriptionAttempt.FailureReason ?? "whisper_words_empty", transcriptionAttempt.FailureDetail);
            }

            var alignmentStopwatch = Stopwatch.StartNew();
            var alignments = AlignLyricsToWordsWithContextualPhonetics(
                lyricSeeds,
                whisperResult.Words,
                cancellationToken: cancellationToken);
            AppendSecondaryAlignmentTrace($"primary-alignment-complete: lines={lyricSeeds.Count}; words={whisperResult.Words.Count}; elapsedMs={alignmentStopwatch.ElapsedMilliseconds}");
            var secondaryAudioPath = SelectSecondaryAlignmentAudioPath(audioFilePath, secondaryAlignmentAudioFilePath);
            if (!string.Equals(secondaryAudioPath, audioFilePath, StringComparison.OrdinalIgnoreCase))
            {
                AppendSecondaryAlignmentTrace($"audio-source: primary={audioFilePath}; secondary={secondaryAudioPath}");
            }

            var correctionStrategy = "line_alignment";
            double? fixedOffsetSeconds = null;
            if (TryApplyStableFixedOffsetAlignment(
                lyricSeeds,
                whisperResult.Words,
                ref alignments,
                ref correctionStrategy,
                ref fixedOffsetSeconds,
                alignmentStopwatch,
                cancellationToken))
            {
                return BuildAlignmentAttemptResult(
                    lyricSeeds,
                    alignments,
                    whisperResult.Words.Count,
                    correctionStrategy,
                    fixedOffsetSeconds);
            }

            alignmentStopwatch.Restart();
            alignments = await TryApplySecondaryAlignmentHintsAsync(secondaryAudioPath, lyricSeeds, alignments, cancellationToken);
            AppendSecondaryAlignmentTrace($"secondary-hints-complete: lines={lyricSeeds.Count}; elapsedMs={alignmentStopwatch.ElapsedMilliseconds}");

            TryApplyStableFixedOffsetAlignment(
                lyricSeeds,
                whisperResult.Words,
                ref alignments,
                ref correctionStrategy,
                ref fixedOffsetSeconds,
                alignmentStopwatch,
                cancellationToken);

            return BuildAlignmentAttemptResult(
                lyricSeeds,
                alignments,
                whisperResult.Words.Count,
                correctionStrategy,
                fixedOffsetSeconds);
        }

        private AlignmentAttemptResult BuildAlignmentAttemptResult(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            int wordCount,
            string correctionStrategy,
            double? fixedOffsetSeconds)
        {
            var alignmentsList = alignments.ToList();
            var matchedCount = alignmentsList.Count(a => a.IsMatched);
            var matchedIndexes = alignmentsList
                .Select((alignment, index) => new { alignment.IsMatched, index })
                .Where(item => item.IsMatched)
                .Select(item => item.index)
                .ToList();
            var unmatchedIndexes = alignmentsList
                .Select((alignment, index) => new { alignment.IsMatched, index })
                .Where(item => !item.IsMatched)
                .Select(item => item.index)
                .ToList();
            AppendSecondaryAlignmentTrace($"alignment-result: matchedIndexes={string.Join(',', matchedIndexes)}; unmatchedIndexes={string.Join(',', unmatchedIndexes)}");
            _logger.LogInformation(
                "AlignLyricsToAudioAsync: {Matched}/{Total} 句完成精準對齊",
                matchedCount,
                alignmentsList.Count);

            return new AlignmentAttemptResult(
                true,
                alignmentsList,
                null,
                null,
                wordCount,
                matchedCount,
                correctionStrategy,
                fixedOffsetSeconds);
        }

        private bool TryApplyStableFixedOffsetAlignment(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<WhisperWord> words,
            ref List<LyricTimingAlignment> alignments,
            ref string correctionStrategy,
            ref double? fixedOffsetSeconds,
            Stopwatch alignmentStopwatch,
            CancellationToken cancellationToken)
        {
            if (HasCompleteMonotonicAlignment(lyricSeeds, alignments))
                return false;

            alignmentStopwatch.Restart();
            var fixedOffset = TryBuildStableFixedOffsetAlignment(lyricSeeds, words, cancellationToken: cancellationToken);
            AppendSecondaryAlignmentTrace($"stable-offset-probe-complete: lines={lyricSeeds.Count}; words={words.Count}; elapsedMs={alignmentStopwatch.ElapsedMilliseconds}; found={(fixedOffset != null)}");
            if (fixedOffset == null)
                return false;

            alignments = fixedOffset.Alignments;
            correctionStrategy = "stable_fixed_offset";
            fixedOffsetSeconds = fixedOffset.OffsetSeconds;
            AppendSecondaryAlignmentTrace(
                $"stable-fixed-offset: offset={fixedOffset.OffsetSeconds:F2}; evidence={fixedOffset.EvidenceCount}; mad={fixedOffset.MedianAbsoluteDeviation:F3}");
            _logger.LogInformation(
                "AlignLyricsToAudioAsync: detected stable fixed offset {Offset:F2}s from {EvidenceCount} evidence lines (MAD={Mad:F3})",
                fixedOffset.OffsetSeconds,
                fixedOffset.EvidenceCount,
                fixedOffset.MedianAbsoluteDeviation);
            return true;
        }

        private static string SelectSecondaryAlignmentAudioPath(string primaryAudioFilePath, string? secondaryAlignmentAudioFilePath)
        {
            if (!string.IsNullOrWhiteSpace(secondaryAlignmentAudioFilePath) && File.Exists(secondaryAlignmentAudioFilePath))
                return secondaryAlignmentAudioFilePath;

            return primaryAudioFilePath;
        }

        private static bool HasCompleteMonotonicAlignment(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments)
        {
            if (lyricSeeds.Count == 0 || alignments.Count != lyricSeeds.Count)
                return false;

            double? previous = null;
            foreach (var alignment in alignments)
            {
                if (!alignment.IsMatched)
                    return false;

                if (previous.HasValue && alignment.Start < previous.Value)
                    return false;

                previous = alignment.Start;
            }

            return true;
        }

        private static string NormalizeInitialWordToken(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return string.Empty;

            return Regex.Replace(word.Trim(), @"\s+", string.Empty);
        }

        private static string NormalizeInitialSegmentText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var normalized = Regex.Replace(text.Trim(), @"\s+", string.Empty);
            normalized = Regex.Replace(normalized, @"(?:\[音楽\]|【音楽】)", string.Empty);
            normalized = Regex.Replace(normalized, @"[♪♫]+", string.Empty);
            return normalized.Trim();
        }

        private static List<WhisperWordTiming> TrimInitialSegmentBoundaryNoiseTokens(IReadOnlyList<WhisperWordTiming> tokens)
        {
            if (tokens == null || tokens.Count == 0)
                return new List<WhisperWordTiming>();

            static bool HasSubstantiveTokens(IReadOnlyList<WhisperWordTiming> items, int startIndex, int endIndex)
            {
                for (var i = startIndex; i <= endIndex; i++)
                {
                    var substantive = Regex.Replace(items[i].Word ?? string.Empty, @"[\p{P}\p{S}\s]", string.Empty);
                    if (substantive.Length >= 2)
                        return true;
                }

                return false;
            }

            var start = 0;
            var end = tokens.Count - 1;

            while (start <= end
                && IsStrongInitialBoundaryNoiseToken(tokens[start].Word)
                && HasSubstantiveTokens(tokens, start + 1, end))
            {
                start++;
            }

            while (end >= start
                && IsStrongInitialBoundaryNoiseToken(tokens[end].Word)
                && HasSubstantiveTokens(tokens, start, end - 1))
            {
                end--;
            }

            if (start > end)
                return new List<WhisperWordTiming>();

            return tokens.Skip(start).Take(end - start + 1).ToList();
        }

        private static bool IsStrongInitialBoundaryNoiseToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return true;

            var substantive = Regex.Replace(token, @"[\p{P}\p{S}\s]", string.Empty);
            if (substantive.Length < 6)
                return false;

            if (!Regex.IsMatch(substantive, @"^[ぁ-んァ-ンa-zA-Z]+$", RegexOptions.CultureInvariant))
                return false;

            return substantive.Distinct().Count() == 1;
        }

        private static bool IsInitialSegmentationNoise(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return true;

            if (Regex.IsMatch(token, @"^[\[【].*(音楽|拍手|笑い|歓声).*[\]】]$"))
                return true;

            return token.All(c => c == '♪' || c == '♫' || char.IsWhiteSpace(c));
        }

        private static bool ShouldKeepInitialSegment(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (Regex.IsMatch(text, @"^[\[【].*[\]】]$"))
                return false;

            var substantiveText = Regex.Replace(text, @"[\p{P}\p{S}\s]", string.Empty);
            if (substantiveText.Length < 2)
                return false;

            if (IsLikelyInitialSegmentVocalizationNoise(substantiveText))
                return false;

            return true;
        }

        private static bool IsLikelyInitialSegmentVocalizationNoise(string substantiveText)
        {
            if (string.IsNullOrWhiteSpace(substantiveText))
                return true;

            if (StartsWithUnresolvableBoundaryNoise(substantiveText))
                return true;

            var distinctChars = substantiveText.Distinct().Count();
            var hasKanji = Regex.IsMatch(substantiveText, @"[\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant);
            var hasLongRepeatedRun = Regex.IsMatch(substantiveText, @"(.)\1{5,}", RegexOptions.CultureInvariant);
            if (!hasKanji && substantiveText.Length >= 8 && distinctChars <= 3)
                return true;

            if (!hasKanji && substantiveText.Length >= 8 && distinctChars <= 4 && hasLongRepeatedRun)
                return true;

            return false;
        }

        private static bool ShouldTreatInitialFallbackAsEmpty(IReadOnlyList<LyricSegment> segments)
        {
            if (segments == null || segments.Count == 0)
                return true;

            if (segments.Count > 3)
                return false;

            return segments.All(segment => IsTinyInterjectionLikeInitialSegment(segment.Japanese));
        }

        private static bool IsTinyInterjectionLikeInitialSegment(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var substantiveText = Regex.Replace(text, @"[\p{P}\p{S}\s]", string.Empty);
            if (string.IsNullOrWhiteSpace(substantiveText))
                return true;

            if (Regex.IsMatch(substantiveText, @"[\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant))
                return false;

            var distinctChars = substantiveText.Distinct().Count();
            return substantiveText.Length <= 8 && distinctChars <= 3;
        }

        private static bool StartsWithUnresolvableBoundaryNoise(string substantiveText)
        {
            var match = Regex.Match(substantiveText, @"^(?<char>[ぁ-んァ-ンa-zA-Z])\k<char>{9,}", RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;

            var trailing = substantiveText[match.Length..];
            return trailing.Length >= 2;
        }

        private static bool EndsWithTerminalPunctuation(string token)
            => !string.IsNullOrWhiteSpace(token) && ".。!！?？".Contains(token[^1]);

        /// <summary>
        /// 純記憶體逐句對齊：給定歌詞與 Whisper words，回傳每句最可能的精確起訖秒數。
        /// </summary>
        public static List<LyricTimingAlignment> AlignLyricsToWordTimings(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<WhisperWordTiming> words,
            double expectedSearchPaddingSeconds = 8)
        {
            var results = new List<LyricTimingAlignment>();
            if (lyricSeeds == null || lyricSeeds.Count == 0)
                return results;

            var safeWords = words?
                .Where(w => !string.IsNullOrWhiteSpace(w.Word))
                .OrderBy(w => w.Start)
                .ToList() ?? new List<WhisperWordTiming>();

            var currentWordIndex = 0;
            for (var lineIndex = 0; lineIndex < lyricSeeds.Count; lineIndex++)
            {
                var seed = lyricSeeds[lineIndex];
                var normalizedLine = NormalizeJapanese(seed.Text);
                if (string.IsNullOrWhiteSpace(normalizedLine))
                {
                    results.Add(new LyricTimingAlignment(seed.Text, seed.ExpectedStart, seed.ExpectedStart, seed.ExpectedStart, 0, false, -1, -1));
                    continue;
                }

                (double Score, double Coverage, double TimeDistance, int StartIndex, int EndIndex)? bestMatch = null;
                for (var startIndex = currentWordIndex; startIndex < safeWords.Count; startIndex++)
                {
                    var startWord = safeWords[startIndex];
                    if (startWord.Start + expectedSearchPaddingSeconds < seed.ExpectedStart)
                        continue;

                    if (startWord.Start > seed.ExpectedStart + expectedSearchPaddingSeconds)
                        break;

                    var candidateText = string.Empty;
                    for (var endIndex = startIndex; endIndex < safeWords.Count && endIndex < startIndex + MAX_WINDOW_WORDS; endIndex++)
                    {
                        candidateText += NormalizeJapanese(safeWords[endIndex].Word);
                        if (string.IsNullOrWhiteSpace(candidateText))
                            continue;

                        var timeDistance = Math.Abs(startWord.Start - seed.ExpectedStart);
                        var coverage = CalculateCoverage(normalizedLine, candidateText);
                        var score = ScoreLineMatch(normalizedLine, candidateText, timeDistance, expectedSearchPaddingSeconds);
                        if (!bestMatch.HasValue || score > bestMatch.Value.Score)
                            bestMatch = (score, coverage, timeDistance, startIndex, endIndex);

                        if (candidateText.Length >= normalizedLine.Length + MAX_EXTRA_CHARS)
                            break;
                    }
                }

                if (bestMatch.HasValue && IsAcceptableLineMatch(bestMatch.Value.Score, bestMatch.Value.Coverage, bestMatch.Value.TimeDistance))
                {
                    var startWord = safeWords[bestMatch.Value.StartIndex];
                    var endWord = safeWords[bestMatch.Value.EndIndex];
                    var resolvedStart = ResolveAlignedStart(startWord.Start, endWord.End, seed.ExpectedStart, bestMatch.Value.Score, bestMatch.Value.StartIndex);
                    results.Add(new LyricTimingAlignment(
                        seed.Text,
                        seed.ExpectedStart,
                        resolvedStart,
                        endWord.End,
                        bestMatch.Value.Score,
                        true,
                        bestMatch.Value.StartIndex,
                        bestMatch.Value.EndIndex));
                    currentWordIndex = bestMatch.Value.EndIndex + 1;
                }
                else
                {
                    var fallbackMatch = FindBestStartFallbackMatch(
                        safeWords,
                        normalizedLine,
                        seed.ExpectedStart,
                        currentWordIndex,
                        expectedSearchPaddingSeconds);

                    if (fallbackMatch.HasValue)
                    {
                        var startWord = safeWords[fallbackMatch.Value.StartIndex];
                        var endWord = safeWords[fallbackMatch.Value.EndIndex];
                        results.Add(new LyricTimingAlignment(
                            seed.Text,
                            seed.ExpectedStart,
                            startWord.Start,
                            endWord.End,
                            fallbackMatch.Value.Score,
                            true,
                            fallbackMatch.Value.StartIndex,
                            fallbackMatch.Value.EndIndex));
                        currentWordIndex = fallbackMatch.Value.EndIndex + 1;
                    }
                    else
                    {
                        results.Add(new LyricTimingAlignment(seed.Text, seed.ExpectedStart, seed.ExpectedStart, seed.ExpectedStart, bestMatch?.Score ?? 0, false, -1, -1));
                    }
                }
            }

            return results;
        }

        public static string BuildComparableJapaneseText(string text, IReadOnlyList<PhoneticToken>? tokens = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var collapsed = CollapseInlineKanaReading(text);
            if (tokens == null || tokens.Count == 0)
                return NormalizeJapanese(collapsed);

            var builder = new System.Text.StringBuilder();
            foreach (var token in tokens)
            {
                var reading = NormalizeJapanese(token.Reading);
                var surface = NormalizeJapanese(token.Surface);
                builder.Append(!string.IsNullOrWhiteSpace(reading) ? reading : surface);
            }

            var phonetic = builder.ToString();
            return string.IsNullOrWhiteSpace(phonetic) ? NormalizeJapanese(collapsed) : phonetic;
        }

        public static string ResolvePythonExecutablePath(IConfiguration configuration)
        {
            var configuredPath = configuration["PythonPath"];
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath;

            if (!OperatingSystem.IsWindows())
                return "python3";

            return WindowsPythonCandidates.FirstOrDefault(File.Exists) ?? "python";
        }

        public static void ApplyFasterWhisperEnvironment(ProcessStartInfo startInfo, IConfiguration configuration)
            => ApplyFasterWhisperEnvironment(startInfo, null, configuration, null);

        public static void ApplyFasterWhisperEnvironment(ProcessStartInfo startInfo, VocalOnsetDetectionOptions? options, IConfiguration configuration, string? modelOverride = null)
        {
            var cacheRoot = !string.IsNullOrWhiteSpace(options?.HuggingFaceCacheRoot)
                ? options.HuggingFaceCacheRoot
                : configuration["HuggingFaceCacheRoot"];
            if (string.IsNullOrWhiteSpace(cacheRoot) && OperatingSystem.IsWindows())
            {
                cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LearnMore",
                    "huggingface");
            }

            if (!string.IsNullOrWhiteSpace(cacheRoot))
            {
                startInfo.Environment["HF_HOME"] = cacheRoot;
                startInfo.Environment["TRANSFORMERS_CACHE"] = Path.Combine(cacheRoot, "transformers");
                startInfo.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(cacheRoot, "hub");
            }

            var modelName = !string.IsNullOrWhiteSpace(modelOverride)
                ? modelOverride
                : configuration["LEARNMORE_FASTER_WHISPER_MODEL"];
            if (!string.IsNullOrWhiteSpace(modelName))
                startInfo.Environment["LEARNMORE_FASTER_WHISPER_MODEL"] = modelName;

            var device = configuration["LEARNMORE_FASTER_WHISPER_DEVICE"];
            if (!string.IsNullOrWhiteSpace(device))
                startInfo.Environment["LEARNMORE_FASTER_WHISPER_DEVICE"] = device;

            var compute = configuration["LEARNMORE_FASTER_WHISPER_COMPUTE"];
            if (!string.IsNullOrWhiteSpace(compute))
                startInfo.Environment["LEARNMORE_FASTER_WHISPER_COMPUTE"] = compute;
        }

        private List<LyricTimingAlignment> AlignLyricsToWordsWithContextualPhonetics(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<WhisperWord> words,
            double expectedSearchPaddingSeconds = 8,
            CancellationToken cancellationToken = default)
        {
            var results = new List<LyricTimingAlignment>();
            if (lyricSeeds == null || lyricSeeds.Count == 0)
                return results;

            var safeWords = words?
                .Where(w => !string.IsNullOrWhiteSpace(w.Word))
                .OrderBy(w => w.Start)
                .ToList() ?? new List<WhisperWord>();
            var comparableWords = BuildComparableWords(safeWords);

            var currentWordIndex = 0;
            for (var lineIndex = 0; lineIndex < lyricSeeds.Count; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seed = lyricSeeds[lineIndex];
                var comparableLine = BuildComparableJapaneseText(seed.Text, ToPhoneticTokens(seed.Text));
                if (string.IsNullOrWhiteSpace(comparableLine))
                {
                    results.Add(new LyricTimingAlignment(seed.Text, seed.ExpectedStart, seed.ExpectedStart, seed.ExpectedStart, 0, false, -1, -1));
                    continue;
                }

                (double Score, double Coverage, double TimeDistance, int StartIndex, int EndIndex)? bestMatch = null;
                for (var startIndex = currentWordIndex; startIndex < comparableWords.Count; startIndex++)
                {
                    if ((startIndex & 0x1f) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var startWord = comparableWords[startIndex].Source;
                    if (startWord.Start + expectedSearchPaddingSeconds < seed.ExpectedStart)
                        continue;

                    if (startWord.Start > seed.ExpectedStart + expectedSearchPaddingSeconds)
                        break;

                    var candidateSurface = new System.Text.StringBuilder();
                    for (var endIndex = startIndex; endIndex < comparableWords.Count && endIndex < startIndex + MAX_WINDOW_WORDS; endIndex++)
                    {
                        candidateSurface.Append(comparableWords[endIndex].ComparableText);
                        var comparableCandidate = candidateSurface.ToString();
                        if (string.IsNullOrWhiteSpace(comparableCandidate))
                            continue;

                        var timeDistance = Math.Abs(startWord.Start - seed.ExpectedStart);
                        var coverage = CalculateCoverage(comparableLine, comparableCandidate);
                        var score = ScoreLineMatch(comparableLine, comparableCandidate, timeDistance, expectedSearchPaddingSeconds);
                        if (!bestMatch.HasValue || score > bestMatch.Value.Score)
                            bestMatch = (score, coverage, timeDistance, startIndex, endIndex);

                        if (comparableCandidate.Length >= comparableLine.Length + MAX_EXTRA_CHARS)
                            break;
                    }
                }

                if (bestMatch.HasValue && IsAcceptableLineMatch(bestMatch.Value.Score, bestMatch.Value.Coverage, bestMatch.Value.TimeDistance))
                {
                    var startWord = comparableWords[bestMatch.Value.StartIndex].Source;
                    var endWord = comparableWords[bestMatch.Value.EndIndex].Source;
                    var resolvedStart = ResolveAlignedStart(startWord.Start, endWord.End, seed.ExpectedStart, bestMatch.Value.Score, bestMatch.Value.StartIndex);
                    results.Add(new LyricTimingAlignment(
                        seed.Text,
                        seed.ExpectedStart,
                        resolvedStart,
                        endWord.End,
                        bestMatch.Value.Score,
                        true,
                        bestMatch.Value.StartIndex,
                        bestMatch.Value.EndIndex));
                    currentWordIndex = bestMatch.Value.EndIndex + 1;
                    continue;
                }

                var fallbackMatch = FindBestContextualStartFallbackMatch(
                    comparableWords,
                    comparableLine,
                    seed.ExpectedStart,
                    currentWordIndex,
                    expectedSearchPaddingSeconds,
                    cancellationToken);

                if (fallbackMatch.HasValue)
                {
                    var startWord = comparableWords[fallbackMatch.Value.StartIndex].Source;
                    var endWord = comparableWords[fallbackMatch.Value.EndIndex].Source;
                    results.Add(new LyricTimingAlignment(
                        seed.Text,
                        seed.ExpectedStart,
                        startWord.Start,
                        endWord.End,
                        fallbackMatch.Value.Score,
                        true,
                        fallbackMatch.Value.StartIndex,
                        fallbackMatch.Value.EndIndex));
                    currentWordIndex = fallbackMatch.Value.EndIndex + 1;
                }
                else
                {
                    results.Add(new LyricTimingAlignment(seed.Text, seed.ExpectedStart, seed.ExpectedStart, seed.ExpectedStart, bestMatch?.Score ?? 0, false, -1, -1));
                }
            }

            return results;
        }

        private StableFixedOffsetResult? TryBuildStableFixedOffsetAlignment(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<WhisperWord> words,
            int maxProbeLines = 16,
            double maxEarlyOffsetSeconds = 30,
            double maxLateOffsetSeconds = 90,
            double minOffsetSeconds = 0.75,
            double maxClusterDistanceSeconds = 1.35,
            double maxMedianAbsoluteDeviationSeconds = 0.85,
            CancellationToken cancellationToken = default)
        {
            if (lyricSeeds.Count == 0 || words.Count == 0)
                return null;

            var safeWords = words
                .Where(word => !string.IsNullOrWhiteSpace(word.Word))
                .OrderBy(word => word.Start)
                .ToList();
            if (safeWords.Count == 0)
                return null;
            var comparableWords = BuildComparableWords(safeWords);

            var normalizedSeenCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var evidence = new List<(double Offset, double Score)>();
            foreach (var seed in lyricSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comparableLine = BuildComparableJapaneseText(seed.Text, ToPhoneticTokens(seed.Text));
                if (comparableLine.Length < 5)
                    continue;

                var seenCount = normalizedSeenCount.GetValueOrDefault(comparableLine);
                if (seenCount >= 2)
                    continue;
                normalizedSeenCount[comparableLine] = seenCount + 1;

                var match = FindBestGlobalContextualLineMatch(
                    comparableWords,
                    comparableLine,
                    seed.ExpectedStart,
                    maxEarlyOffsetSeconds,
                    maxLateOffsetSeconds,
                    cancellationToken);
                if (match.HasValue)
                    evidence.Add((match.Value.Start - seed.ExpectedStart, match.Value.Score));

                if (evidence.Count >= maxProbeLines)
                    break;
            }

            if (evidence.Count < 4)
                return null;

            var median = Median(evidence.Select(item => item.Offset));
            if (Math.Abs(median) < minOffsetSeconds || median < -maxEarlyOffsetSeconds || median > maxLateOffsetSeconds)
                return null;

            var cluster = evidence
                .Where(item => Math.Abs(item.Offset - median) <= maxClusterDistanceSeconds)
                .ToList();
            var requiredClusterCount = Math.Max(4, (int)Math.Ceiling(evidence.Count * 0.60));
            if (cluster.Count < requiredClusterCount)
                return null;

            var clusterMedian = Median(cluster.Select(item => item.Offset));
            var mad = Median(cluster.Select(item => Math.Abs(item.Offset - clusterMedian)));
            if (mad > maxMedianAbsoluteDeviationSeconds)
                return null;

            var roundedOffset = Math.Round(clusterMedian, 2);
            var averageScore = cluster.Average(item => item.Score);
            var alignments = lyricSeeds
                .Select(seed =>
                {
                    var shiftedStart = Math.Round(Math.Max(0, seed.ExpectedStart + roundedOffset), 2);
                    return new LyricTimingAlignment(
                        seed.Text,
                        seed.ExpectedStart,
                        shiftedStart,
                        shiftedStart,
                        averageScore,
                        true,
                        -1,
                        -1);
                })
                .ToList();

            return HasCompleteMonotonicAlignment(lyricSeeds, alignments)
                ? new StableFixedOffsetResult(roundedOffset, mad, cluster.Count, alignments)
                : null;
        }

        private (double Start, double Score)? FindBestGlobalContextualLineMatch(
            IReadOnlyList<ComparableWhisperWord> safeWords,
            string comparableLine,
            double expectedStart,
            double maxEarlyOffsetSeconds,
            double maxLateOffsetSeconds,
            CancellationToken cancellationToken = default)
        {
            (double Start, double Score, double Coverage)? best = null;
            for (var startIndex = 0; startIndex < safeWords.Count; startIndex++)
            {
                if ((startIndex & 0x1f) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var startWord = safeWords[startIndex].Source;
                var offset = startWord.Start - expectedStart;
                if (offset < -maxEarlyOffsetSeconds || offset > maxLateOffsetSeconds)
                    continue;

                var candidateSurface = new System.Text.StringBuilder();
                for (var endIndex = startIndex; endIndex < safeWords.Count && endIndex < startIndex + MAX_WINDOW_WORDS; endIndex++)
                {
                    candidateSurface.Append(safeWords[endIndex].ComparableText);
                    var comparableCandidate = candidateSurface.ToString();
                    if (string.IsNullOrWhiteSpace(comparableCandidate))
                        continue;

                    var coverage = CalculateCoverage(comparableLine, comparableCandidate);
                    var score = ScoreLineMatch(comparableLine, comparableCandidate, 0, 0);
                    if (!best.HasValue || score > best.Value.Score)
                        best = (startWord.Start, score, coverage);

                    if (comparableCandidate.Length >= comparableLine.Length + MAX_EXTRA_CHARS)
                        break;
                }
            }

            if (!best.HasValue)
                return null;

            return best.Value.Score >= 0.84 || (best.Value.Score >= 0.78 && best.Value.Coverage >= 0.90)
                ? (best.Value.Start, best.Value.Score)
                : null;
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            if (sorted.Count == 0)
                return 0;

            var midpoint = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[midpoint]
                : (sorted[midpoint - 1] + sorted[midpoint]) / 2.0;
        }

        private static (double Score, int StartIndex, int EndIndex)? FindBestStartFallbackMatch(
            IReadOnlyList<WhisperWordTiming> safeWords,
            string normalizedLine,
            double expectedStart,
            int currentWordIndex,
            double expectedSearchPaddingSeconds)
        {
            (double Score, int StartIndex, int EndIndex)? bestFallback = null;
            for (var startIndex = currentWordIndex; startIndex < safeWords.Count; startIndex++)
            {
                var startWord = safeWords[startIndex];
                if (startWord.Start + expectedSearchPaddingSeconds < expectedStart)
                    continue;

                if (startWord.Start > expectedStart + expectedSearchPaddingSeconds)
                    break;

                var candidateText = string.Empty;
                for (var endIndex = startIndex; endIndex < safeWords.Count && endIndex < startIndex + MAX_WINDOW_WORDS; endIndex++)
                {
                    candidateText += NormalizeJapanese(safeWords[endIndex].Word);
                    if (string.IsNullOrWhiteSpace(candidateText))
                        continue;

                    var coverage = CalculateCoverage(normalizedLine, candidateText);
                    if (coverage < START_FALLBACK_MIN_COVERAGE)
                        continue;

                    var prefixScore = ScoreStartPrefixMatch(
                        normalizedLine,
                        candidateText,
                        Math.Abs(startWord.Start - expectedStart),
                        expectedSearchPaddingSeconds);

                    if (!bestFallback.HasValue || prefixScore > bestFallback.Value.Score)
                        bestFallback = (prefixScore, startIndex, endIndex);

                    if (candidateText.Length >= normalizedLine.Length + MAX_EXTRA_CHARS)
                        break;
                }
            }

            return bestFallback.HasValue && bestFallback.Value.Score >= START_FALLBACK_THRESHOLD
                ? bestFallback
                : null;
        }

        private (double Score, int StartIndex, int EndIndex)? FindBestContextualStartFallbackMatch(
            IReadOnlyList<ComparableWhisperWord> safeWords,
            string comparableLine,
            double expectedStart,
            int currentWordIndex,
            double expectedSearchPaddingSeconds,
            CancellationToken cancellationToken = default)
        {
            (double Score, int StartIndex, int EndIndex)? bestFallback = null;
            for (var startIndex = currentWordIndex; startIndex < safeWords.Count; startIndex++)
            {
                if ((startIndex & 0x1f) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var startWord = safeWords[startIndex].Source;
                if (startWord.Start + expectedSearchPaddingSeconds < expectedStart)
                    continue;

                if (startWord.Start > expectedStart + expectedSearchPaddingSeconds)
                    break;

                var candidateSurface = new System.Text.StringBuilder();
                for (var endIndex = startIndex; endIndex < safeWords.Count && endIndex < startIndex + MAX_WINDOW_WORDS; endIndex++)
                {
                    candidateSurface.Append(safeWords[endIndex].ComparableText);
                    var comparableCandidate = candidateSurface.ToString();
                    if (string.IsNullOrWhiteSpace(comparableCandidate))
                        continue;

                    var coverage = CalculateCoverage(comparableLine, comparableCandidate);
                    if (coverage < START_FALLBACK_MIN_COVERAGE)
                        continue;

                    var prefixScore = ScoreStartPrefixMatch(
                        comparableLine,
                        comparableCandidate,
                        Math.Abs(startWord.Start - expectedStart),
                        expectedSearchPaddingSeconds);

                    if (!bestFallback.HasValue || prefixScore > bestFallback.Value.Score)
                        bestFallback = (prefixScore, startIndex, endIndex);

                    if (comparableCandidate.Length >= comparableLine.Length + MAX_EXTRA_CHARS)
                        break;
                }
            }

            return bestFallback.HasValue && bestFallback.Value.Score >= START_FALLBACK_THRESHOLD
                ? bestFallback
                : null;
        }

        private List<ComparableWhisperWord> BuildComparableWords(IReadOnlyList<WhisperWord> words)
        {
            var comparableWords = new List<ComparableWhisperWord>(words.Count);
            foreach (var word in words)
            {
                var comparable = BuildComparableJapaneseText(word.Word, ToPhoneticTokens(word.Word));
                comparableWords.Add(new ComparableWhisperWord(word, comparable));
            }

            return comparableWords;
        }

        /// <summary>
        /// 舊版單句比對方法（保留相容性）
        /// </summary>
        public async Task<double> CalculateOffsetByLyricsAsync(
            string audioFilePath,
            double lrcFirstTimestamp,
            string lrcFirstLine,
            CancellationToken cancellationToken = default)
        {
            var lrcLines = new List<(double, string)> { (lrcFirstTimestamp, lrcFirstLine) };
            return await CalculateOffsetByLyricsAsync(audioFilePath, lrcLines, cancellationToken);
        }

        /// <summary>
        /// 在 Whisper words 中尋找最佳匹配
        /// </summary>
        private (double whisperTime, string matchedWord)? FindBestWordMatch(
            List<WhisperWord> words, string lrcLyrics, double lrcTimestamp)
        {
            var normalizedLrc = NormalizeJapanese(lrcLyrics);
            if (normalizedLrc.Length < MIN_WORD_LENGTH) return null;

            // 提取 LRC 前幾個字作為搜尋目標
            var lrcFirstChars = normalizedLrc.Substring(0, Math.Min(6, normalizedLrc.Length));

            // 策略 1：在連續的 words 中尋找包含 LRC 開頭的位置
            var concatenated = "";
            var wordStartIndex = 0;

            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var normalizedWord = NormalizeJapanese(word.Word);

                if (string.IsNullOrEmpty(normalizedWord) || normalizedWord.Length < MIN_WORD_LENGTH)
                    continue;

                concatenated += normalizedWord;

                // 檢查是否包含 LRC 開頭
                var matchIndex = concatenated.IndexOf(lrcFirstChars, StringComparison.Ordinal);
                if (matchIndex >= 0)
                {
                    // 找到匹配，回溯找到對應的 word
                    var charCount = 0;
                    for (int j = wordStartIndex; j <= i; j++)
                    {
                        var w = words[j];
                        var nw = NormalizeJapanese(w.Word);
                        if (string.IsNullOrEmpty(nw)) continue;

                        if (charCount <= matchIndex && matchIndex < charCount + nw.Length)
                        {
                            // 信心度檢查：時間應該在 LRC 時間附近的合理範圍內
                            // 允許 YouTube 影片最多延後 60 秒
                            if (w.Start >= 0 && w.Start < lrcTimestamp + 90)
                            {
                                return (w.Start, w.Word);
                            }
                        }
                        charCount += nw.Length;
                    }
                }

                // 滑動窗口：如果累積太長就重置
                if (concatenated.Length > 100)
                {
                    concatenated = normalizedWord;
                    wordStartIndex = i;
                }
            }

            // 策略 2：單字直接比對（相似度）
            foreach (var word in words)
            {
                var normalizedWord = NormalizeJapanese(word.Word);
                if (string.IsNullOrEmpty(normalizedWord) || normalizedWord.Length < MIN_WORD_LENGTH)
                    continue;

                // 檢查是否包含或被包含
                if (normalizedWord.Contains(lrcFirstChars) || lrcFirstChars.Contains(normalizedWord))
                {
                    if (word.Start >= 0 && word.Start < lrcTimestamp + 90)
                    {
                        return (word.Start, word.Word);
                    }
                }

                // 相似度比對
                var similarity = CalculateSimilarity(lrcFirstChars, normalizedWord);
                if (similarity >= SIMILARITY_THRESHOLD && word.Start >= 0 && word.Start < lrcTimestamp + 90)
                {
                    return (word.Start, word.Word);
                }
            }

            return null;
        }

        /// <summary>
        /// 計算穩健平均值（排除異常值）
        /// </summary>
        private double CalculateRobustAverage(List<double> values)
        {
            if (values.Count == 0) return 0;
            if (values.Count == 1) return values[0];
            if (values.Count == 2) return values.Average();

            // 排序後去掉最大和最小值
            var sorted = values.OrderBy(x => x).ToList();
            var trimmed = sorted.Skip(1).Take(sorted.Count - 2).ToList();

            return trimmed.Count > 0 ? trimmed.Average() : sorted.Average();
        }

        private static double ScoreLineMatch(string normalizedLine, string candidateText, double timeDistanceSeconds, double expectedSearchPaddingSeconds)
        {
            if (string.IsNullOrWhiteSpace(normalizedLine) || string.IsNullOrWhiteSpace(candidateText))
                return 0;

            var similarity = CalculateSimilarity(normalizedLine, candidateText);
            var shorter = Math.Min(normalizedLine.Length, candidateText.Length);
            var longer = Math.Max(normalizedLine.Length, candidateText.Length);
            var coverage = longer > 0 ? (double)shorter / longer : 0;
            var isContainment = normalizedLine.Contains(candidateText, StringComparison.Ordinal) ||
                               candidateText.Contains(normalizedLine, StringComparison.Ordinal);

            if (isContainment)
            {
                // 只在覆蓋率夠高時才給 containment bonus，避免「少年よ」這種半句誤判成整句。
                var containmentScore = coverage >= 0.85
                    ? coverage + 0.35
                    : coverage >= 0.70
                        ? coverage + 0.12
                        : coverage - 0.08;
                similarity = Math.Max(similarity, containmentScore);
            }

            // 覆蓋率太低時額外扣分，避免晚到的半句/尾句因時間接近而贏過完整匹配。
            if (coverage < 0.70)
                similarity -= (0.70 - coverage) * 0.35;

            if (expectedSearchPaddingSeconds <= 0)
                return similarity;

            var normalizedDistance = Math.Min(1.0, timeDistanceSeconds / expectedSearchPaddingSeconds);
            return similarity - (normalizedDistance * 0.08);
        }

        private static double ScoreStartPrefixMatch(string normalizedLine, string candidateText, double timeDistanceSeconds, double expectedSearchPaddingSeconds)
        {
            if (string.IsNullOrWhiteSpace(normalizedLine) || string.IsNullOrWhiteSpace(candidateText))
                return 0;

            var expectedPrefix = normalizedLine[..Math.Min(normalizedLine.Length, candidateText.Length)];
            var similarity = CalculateSimilarity(expectedPrefix, candidateText);
            var coverage = CalculateCoverage(normalizedLine, candidateText);

            // start fallback 只拿來校正 start，容許較重的尾段 ASR 漂移，但仍要求前半句很像。
            if (coverage >= START_FALLBACK_MIN_COVERAGE)
                similarity += (coverage - START_FALLBACK_MIN_COVERAGE) * 0.12;

            if (expectedSearchPaddingSeconds <= 0)
                return similarity;

            var normalizedDistance = Math.Min(1.0, timeDistanceSeconds / expectedSearchPaddingSeconds);
            return similarity - (normalizedDistance * 0.06);
        }

        private static bool IsAcceptableLineMatch(double score, double coverage, double timeDistanceSeconds)
        {
            if (score >= LINE_MATCH_THRESHOLD)
                return true;

            return score >= HIGH_COVERAGE_RESCUE_THRESHOLD
                && coverage >= HIGH_COVERAGE_RESCUE_MIN_COVERAGE
                && timeDistanceSeconds <= HIGH_COVERAGE_RESCUE_MAX_TIME_DISTANCE;
        }

        private static double ResolveAlignedStart(double matchedStart, double matchedStartEnd, double expectedStart, double score, int startIndex)
        {
            if (startIndex == 0
                && matchedStart <= 0.05
                && expectedStart > 0.5
                && expectedStart < matchedStartEnd
                && (matchedStartEnd - matchedStart) >= EARLY_TOKEN_CLAMP_MIN_DURATION)
            {
                return expectedStart;
            }

            if (score < LINE_MATCH_THRESHOLD
                && matchedStart < expectedStart
                && (expectedStart - matchedStart) <= LOW_SCORE_START_CLAMP_MAX_LEAD
                && expectedStart < matchedStartEnd)
            {
                return expectedStart;
            }

            return matchedStart;
        }

        private static double CalculateCoverage(string normalizedLine, string candidateText)
        {
            var shorter = Math.Min(normalizedLine.Length, candidateText.Length);
            var longer = Math.Max(normalizedLine.Length, candidateText.Length);
            return longer > 0 ? (double)shorter / longer : 0;
        }

        public sealed record SecondaryAlignmentSignal(
            double Start,
            double End,
            string Text,
            double FullSimilarity,
            double PrefixSimilarity,
            double SuffixSimilarity,
            int SegmentCount = 1,
            double AnchorFullSimilarity = 0,
            double AnchorPrefixSimilarity = 0,
            double AnchorSuffixSimilarity = 0);

        public sealed record SecondaryAlignmentSegment(double Start, double End, string Text);

        public sealed record SecondaryAlignmentWindow(double Start, double End);

        private static string? GetSecondaryAlignmentTargetReason(
            LyricTimingSeed seed,
            LyricTimingAlignment alignment,
            double minLateStartSeconds = 0.5,
            double maxLateStartSeconds = 2.5,
            double closeToSeedSeconds = 0.5,
            double moderateScoreThreshold = 1.0,
            double longLineDurationSeconds = 5.0,
            double minUnmatchedRiskScore = 0.42,
            double maxUnmatchedRiskScore = 0.60,
            int minUnmatchedRiskChars = 18)
        {
            if (!alignment.IsMatched)
            {
                var normalizedLength = NormalizeJapanese(seed.Text).Length;
                if (alignment.Score >= minUnmatchedRiskScore
                    && alignment.Score <= maxUnmatchedRiskScore
                    && normalizedLength >= minUnmatchedRiskChars)
                {
                    return "unmatched-medium-score-long";
                }

                return null;
            }

            var lateStart = alignment.Start - seed.ExpectedStart;
            if (lateStart >= minLateStartSeconds && lateStart <= maxLateStartSeconds)
                return "late-start";

            var lineDuration = Math.Max(0, alignment.End - alignment.Start);
            if (Math.Abs(lateStart) <= closeToSeedSeconds
                && alignment.Score <= moderateScoreThreshold
                && lineDuration >= longLineDurationSeconds)
            {
                return "close-seed-moderate-long";
            }

            return null;
        }

        private static List<int> GetSecondaryAlignmentTargetLineIndexes(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            double minLateStartSeconds = 0.5,
            double maxLateStartSeconds = 2.5)
        {
            var indexes = new List<int>();
            if (lyricSeeds == null || alignments == null)
                return indexes;

            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                var seed = lyricSeeds[i];
                var alignment = alignments[i];
                var reason = GetSecondaryAlignmentTargetReason(seed, alignment, minLateStartSeconds, maxLateStartSeconds);
                if (reason != null)
                    indexes.Add(i);
            }

            return indexes;
        }

        public static List<SecondaryAlignmentWindow> BuildSecondaryAlignmentWindows(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            double minLateStartSeconds = 0.5,
            double maxLateStartSeconds = 2.5,
            double prePaddingSeconds = 3.0,
            double postPaddingSeconds = 3.5,
            double minWindowSeconds = 8.0)
        {
            var windows = new List<SecondaryAlignmentWindow>();
            if (lyricSeeds == null || alignments == null)
                return windows;

            foreach (var i in GetSecondaryAlignmentTargetLineIndexes(
                lyricSeeds,
                alignments,
                minLateStartSeconds,
                maxLateStartSeconds))
            {
                var seed = lyricSeeds[i];
                var alignment = alignments[i];

                var start = Math.Max(0, seed.ExpectedStart - prePaddingSeconds);
                var end = Math.Max(alignment.End, alignment.Start) + postPaddingSeconds;
                if ((end - start) < minWindowSeconds)
                    end = start + minWindowSeconds;

                windows.Add(new SecondaryAlignmentWindow(start, end));
            }

            if (windows.Count <= 1)
                return windows;

            var merged = new List<SecondaryAlignmentWindow>();
            foreach (var window in windows.OrderBy(w => w.Start))
            {
                if (merged.Count == 0)
                {
                    merged.Add(window);
                    continue;
                }

                var last = merged[^1];
                if (window.Start <= last.End)
                {
                    merged[^1] = new SecondaryAlignmentWindow(last.Start, Math.Max(last.End, window.End));
                }
                else
                {
                    merged.Add(window);
                }
            }

            return merged;
        }

        public static List<SecondaryAlignmentWindow> BuildFocusedSecondaryAlignmentWindows(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            double prePaddingSeconds = 3.0,
            double postPaddingSeconds = 5.7,
            double minWindowSeconds = 8.0,
            double minUnmatchedRiskScore = 0.38,
            double maxUnmatchedRiskScore = 0.60)
        {
            var windows = new List<SecondaryAlignmentWindow>();
            if (lyricSeeds == null || alignments == null)
                return windows;

            var repeatedShortSeedKeys = lyricSeeds
                .Where(seed => !string.IsNullOrWhiteSpace(seed.Text))
                .GroupBy(seed => NormalizeJapanese(seed.Text))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                    && group.Key.Length <= 8
                    && group.Count() >= 2)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            var repeatedShortIndexes = new HashSet<int>();
            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                var seed = lyricSeeds[i];
                var alignment = alignments[i];
                var reason = GetSecondaryAlignmentTargetReason(
                    seed,
                    alignment,
                    minUnmatchedRiskScore: minUnmatchedRiskScore,
                    maxUnmatchedRiskScore: maxUnmatchedRiskScore);
                var normalizedSeed = NormalizeJapanese(seed.Text);
                var isRepeatedShortUnmatched = !alignment.IsMatched
                    && repeatedShortSeedKeys.Contains(normalizedSeed)
                    && alignment.Score >= 0.15
                    && alignment.Score <= maxUnmatchedRiskScore;
                if (alignment.IsMatched || (reason == null && !isRepeatedShortUnmatched))
                    continue;

                var start = Math.Max(0, seed.ExpectedStart - prePaddingSeconds);
                var end = seed.ExpectedStart + postPaddingSeconds;
                if ((end - start) < minWindowSeconds)
                    end = start + minWindowSeconds;

                windows.Add(new SecondaryAlignmentWindow(start, end));
                if (isRepeatedShortUnmatched)
                    repeatedShortIndexes.Add(i);
            }

            const double contextualLongLineMinScore = 0.25;
            const int contextualLongLineMinLength = 12;
            const double repeatedShortContextSeconds = 16.0;
            for (var i = 0; i < count; i++)
            {
                if (repeatedShortIndexes.Contains(i))
                    continue;

                var seed = lyricSeeds[i];
                var alignment = alignments[i];
                var normalizedSeed = NormalizeJapanese(seed.Text);
                if (alignment.IsMatched
                    || normalizedSeed.Length < contextualLongLineMinLength
                    || alignment.Score < contextualLongLineMinScore
                    || alignment.Score > maxUnmatchedRiskScore)
                {
                    continue;
                }

                var isNearRepeatedShortCluster = repeatedShortIndexes.Any(index =>
                    Math.Abs(lyricSeeds[index].ExpectedStart - seed.ExpectedStart) <= repeatedShortContextSeconds);
                if (!isNearRepeatedShortCluster)
                    continue;

                var start = Math.Max(0, seed.ExpectedStart - prePaddingSeconds);
                var end = seed.ExpectedStart + postPaddingSeconds;
                if ((end - start) < minWindowSeconds)
                    end = start + minWindowSeconds;
                windows.Add(new SecondaryAlignmentWindow(start, end));
            }

            const int isolatedTailMinLength = 8;
            const double isolatedTailMinGapSeconds = 20.0;
            var lastUnmatchedIndex = -1;
            for (var i = count - 1; i >= 0; i--)
            {
                if (!alignments[i].IsMatched)
                {
                    lastUnmatchedIndex = i;
                    break;
                }
            }

            if (lastUnmatchedIndex >= 0)
            {
                var seed = lyricSeeds[lastUnmatchedIndex];
                var normalizedSeed = NormalizeJapanese(seed.Text);
                var previousExpectedStart = lastUnmatchedIndex > 0 ? lyricSeeds[lastUnmatchedIndex - 1].ExpectedStart : 0;
                var isIsolatedTail = (seed.ExpectedStart - previousExpectedStart) >= isolatedTailMinGapSeconds;
                var alreadyCovered = windows.Any(window => seed.ExpectedStart >= window.Start && seed.ExpectedStart <= window.End);
                if (normalizedSeed.Length >= isolatedTailMinLength && isIsolatedTail && !alreadyCovered)
                {
                    var start = Math.Max(0, seed.ExpectedStart - prePaddingSeconds);
                    var end = seed.ExpectedStart + postPaddingSeconds;
                    if ((end - start) < minWindowSeconds)
                        end = start + minWindowSeconds;
                    windows.Add(new SecondaryAlignmentWindow(start, end));
                }
            }

            if (windows.Count <= 1)
                return windows;

            const double mergeGapSeconds = 1.0;
            var merged = new List<SecondaryAlignmentWindow>();
            foreach (var window in windows.OrderBy(w => w.Start))
            {
                if (merged.Count == 0)
                {
                    merged.Add(window);
                    continue;
                }

                var last = merged[^1];
                if (window.Start <= last.End + mergeGapSeconds)
                {
                    merged[^1] = new SecondaryAlignmentWindow(last.Start, Math.Max(last.End, window.End));
                }
                else
                {
                    merged.Add(window);
                }
            }

            return merged;
        }

        public static bool HasSecondaryAlignmentWork(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments)
        {
            var broadWindows = BuildSecondaryAlignmentWindows(lyricSeeds, alignments);
            if (broadWindows.Count > 0)
                return true;

            var focusedWindows = BuildFocusedSecondaryAlignmentWindows(lyricSeeds, alignments);
            return focusedWindows.Count > 0;
        }

        private static IEnumerable<string> BuildSecondaryAlignmentWindowDiagnostics(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            double minLateStartSeconds = 0.5,
            double maxLateStartSeconds = 2.5)
        {
            if (lyricSeeds == null || alignments == null)
                yield break;

            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                var seed = lyricSeeds[i];
                var alignment = alignments[i];
                if (!alignment.IsMatched)
                {
                    yield return $"line[{i}] expected={seed.ExpectedStart:F2} current=unmatched end=unmatched delta=n/a duration=n/a score={alignment.Score:F3} inWindow=False reason=unmatched text={seed.Text}";
                    continue;
                }

                var lateStart = alignment.Start - seed.ExpectedStart;
                var reason = GetSecondaryAlignmentTargetReason(seed, alignment, minLateStartSeconds, maxLateStartSeconds) ?? "none";
                var inWindow = reason != "none";
                var duration = Math.Max(0, alignment.End - alignment.Start);
                yield return $"line[{i}] expected={seed.ExpectedStart:F2} current={alignment.Start:F2} end={alignment.End:F2} delta={lateStart:F2} duration={duration:F2} score={alignment.Score:F3} inWindow={inWindow} reason={reason} text={seed.Text}";
            }
        }

        public static SecondaryAlignmentSignal EvaluateSecondaryAlignmentSegment(
            string lyricText,
            string segmentText,
            double segmentStart,
            double segmentEnd)
        {
            var normalizedLyric = NormalizeJapanese(lyricText);
            var normalizedSegment = NormalizeJapanese(segmentText);

            if (string.IsNullOrWhiteSpace(normalizedLyric) || string.IsNullOrWhiteSpace(normalizedSegment))
                return new SecondaryAlignmentSignal(segmentStart, segmentEnd, segmentText, 0, 0, 0);

            var prefixLength = Math.Min(12, Math.Min(normalizedLyric.Length, normalizedSegment.Length));
            var suffixLength = Math.Min(16, Math.Min(normalizedLyric.Length, normalizedSegment.Length));

            var fullSimilarity = CalculateSimilarity(normalizedLyric, normalizedSegment);
            var prefixSimilarity = prefixLength > 0
                ? CalculateSimilarity(normalizedLyric[..prefixLength], normalizedSegment[..prefixLength])
                : 0;
            var suffixSimilarity = suffixLength > 0
                ? CalculateSimilarity(normalizedLyric[^suffixLength..], normalizedSegment[^suffixLength..])
                : 0;

            return new SecondaryAlignmentSignal(
                segmentStart,
                segmentEnd,
                segmentText,
                fullSimilarity,
                prefixSimilarity,
                suffixSimilarity);
        }

        public static bool ShouldUseSecondaryAlignmentStart(
            double currentStart,
            SecondaryAlignmentSignal signal,
            double minFullSimilarity = 0.80,
            double minPrefixSimilarity = 0.58,
            double minSuffixSimilarity = 0.93)
        {
            return signal.Start < currentStart
                && signal.FullSimilarity >= minFullSimilarity
                && signal.PrefixSimilarity >= minPrefixSimilarity
                && signal.SuffixSimilarity >= minSuffixSimilarity;
        }

        private static bool ShouldUseSplitCombinedSecondaryAlignmentStart(
            double currentStart,
            double currentEnd,
            SecondaryAlignmentSignal signal,
            double maxLeadSeconds = 2.5,
            double maxEndOvershootSeconds = 1.25,
            double minCombinedFullSimilarity = 0.50,
            double minCombinedSuffixSimilarity = 0.68,
            double minAnchorFullSimilarity = 0.40,
            double minAnchorSuffixSimilarity = 0.88)
        {
            return signal.SegmentCount >= 2
                && signal.Start < currentStart
                && (currentStart - signal.Start) <= maxLeadSeconds
                && signal.End >= currentEnd
                && (signal.End - currentEnd) <= maxEndOvershootSeconds
                && signal.FullSimilarity >= minCombinedFullSimilarity
                && signal.SuffixSimilarity >= minCombinedSuffixSimilarity
                && signal.AnchorFullSimilarity >= minAnchorFullSimilarity
                && signal.AnchorSuffixSimilarity >= minAnchorSuffixSimilarity;
        }

        private static bool ShouldUseModerateSingleSegmentSecondaryAlignmentStartForMatched(
            LyricTimingAlignment alignment,
            SecondaryAlignmentSignal signal,
            double maxLeadSeconds = 2.0,
            double maxEndOvershootSeconds = 0.8,
            double minFullSimilarity = 0.68,
            double minPrefixSimilarity = 0.55,
            double minSuffixSimilarity = 0.65)
        {
            return alignment.IsMatched
                && signal.SegmentCount == 1
                && signal.Start < alignment.Start
                && (alignment.Start - signal.Start) <= maxLeadSeconds
                && signal.End >= alignment.End
                && (signal.End - alignment.End) <= maxEndOvershootSeconds
                && signal.FullSimilarity >= minFullSimilarity
                && signal.PrefixSimilarity >= minPrefixSimilarity
                && signal.SuffixSimilarity >= minSuffixSimilarity;
        }

        private static bool ShouldUseModerateSingleSegmentSecondaryAlignmentStartForUnmatched(
            LyricTimingAlignment alignment,
            SecondaryAlignmentSignal signal,
            double maxLeadSeconds = 3.5,
            double minFullSimilarity = 0.50,
            double minSuffixSimilarity = 0.55)
        {
            return !alignment.IsMatched
                && signal.SegmentCount == 1
                && signal.Start < alignment.ExpectedStart
                && (alignment.ExpectedStart - signal.Start) <= maxLeadSeconds
                && signal.End >= alignment.ExpectedStart
                && signal.FullSimilarity >= minFullSimilarity
                && signal.SuffixSimilarity >= minSuffixSimilarity;
        }

        private static bool ShouldUseSplitCombinedSecondaryAlignmentStartForUnmatched(
            LyricTimingAlignment alignment,
            SecondaryAlignmentSignal signal,
            double maxLeadSeconds = 3.5,
            double minCombinedFullSimilarity = 0.30,
            double minCombinedSuffixSimilarity = 0.30,
            double minAnchorFullSimilarity = 0.28,
            double minAnchorSuffixSimilarity = 0.45)
        {
            return !alignment.IsMatched
                && signal.SegmentCount >= 2
                && signal.Start < alignment.ExpectedStart
                && (alignment.ExpectedStart - signal.Start) <= maxLeadSeconds
                && signal.End >= alignment.ExpectedStart
                && signal.FullSimilarity >= minCombinedFullSimilarity
                && signal.SuffixSimilarity >= minCombinedSuffixSimilarity
                && signal.AnchorFullSimilarity >= minAnchorFullSimilarity
                && signal.AnchorSuffixSimilarity >= minAnchorSuffixSimilarity;
        }

        private static bool ShouldUseLongUnmatchedSplitCombinedSecondaryAlignmentStart(
            LyricTimingAlignment alignment,
            SecondaryAlignmentSignal signal,
            double maxLeadSeconds = 2.0,
            double minCombinedFullSimilarity = 0.30,
            double minPrefixSimilarity = 0.16,
            double minAnchorFullSimilarity = 0.22,
            int minNormalizedLength = 16)
        {
            return !alignment.IsMatched
                && NormalizeJapanese(alignment.Text).Length >= minNormalizedLength
                && signal.SegmentCount >= 3
                && signal.Start < alignment.ExpectedStart
                && (alignment.ExpectedStart - signal.Start) <= maxLeadSeconds
                && signal.End >= alignment.ExpectedStart
                && signal.FullSimilarity >= minCombinedFullSimilarity
                && signal.PrefixSimilarity >= minPrefixSimilarity
                && signal.AnchorFullSimilarity >= minAnchorFullSimilarity;
        }

        private static bool ShouldUseSecondaryAlignmentHintStart(
            LyricTimingAlignment alignment,
            SecondaryAlignmentSignal signal)
        {
            return ShouldUseSecondaryAlignmentStart(alignment.Start, signal)
                || ShouldUseModerateSingleSegmentSecondaryAlignmentStartForMatched(alignment, signal)
                || ShouldUseModerateSingleSegmentSecondaryAlignmentStartForUnmatched(alignment, signal)
                || ShouldUseSplitCombinedSecondaryAlignmentStart(alignment.Start, alignment.End, signal)
                || ShouldUseSplitCombinedSecondaryAlignmentStartForUnmatched(alignment, signal)
                || ShouldUseLongUnmatchedSplitCombinedSecondaryAlignmentStart(alignment, signal);
        }

        public static Dictionary<int, SecondaryAlignmentSignal> BuildSecondaryAlignmentHints(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            IReadOnlyList<SecondaryAlignmentSegment> segments,
            double expectedSearchPaddingSeconds = 8)
        {
            var hints = new Dictionary<int, SecondaryAlignmentSignal>();
            if (lyricSeeds == null || alignments == null || segments == null)
                return hints;

            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                var seed = lyricSeeds[i];
                var alignment = alignments[i];

                SecondaryAlignmentSignal? bestSignal = null;
                foreach (var signal in EnumerateSecondaryAlignmentSignals(seed.Text, segments, seed.ExpectedStart, alignment.End, expectedSearchPaddingSeconds))
                {
                    if (!ShouldUseSecondaryAlignmentHintStart(alignment, signal))
                        continue;

                    if (bestSignal == null
                        || signal.FullSimilarity > bestSignal.FullSimilarity
                        || (Math.Abs(signal.FullSimilarity - bestSignal.FullSimilarity) < 0.0001 && signal.SuffixSimilarity > bestSignal.SuffixSimilarity)
                        || (Math.Abs(signal.FullSimilarity - bestSignal.FullSimilarity) < 0.0001 && Math.Abs(signal.SuffixSimilarity - bestSignal.SuffixSimilarity) < 0.0001 && signal.PrefixSimilarity > bestSignal.PrefixSimilarity)
                        || (Math.Abs(signal.FullSimilarity - bestSignal.FullSimilarity) < 0.0001 && Math.Abs(signal.SuffixSimilarity - bestSignal.SuffixSimilarity) < 0.0001 && Math.Abs(signal.PrefixSimilarity - bestSignal.PrefixSimilarity) < 0.0001 && signal.Start < bestSignal.Start))
                    {
                        bestSignal = signal;
                    }
                }

                if (bestSignal != null)
                    hints[i] = bestSignal;
            }

            return hints;
        }

        public static bool HasViableFocusedSecondaryAlignmentCandidate(
            SecondaryAlignmentWindow window,
            IReadOnlyList<SecondaryAlignmentSegment> segments,
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            double expectedPaddingSeconds = 1.0,
            double minFullSimilarity = 0.32,
            double minPrefixSimilarity = 0.55,
            double minSuffixSimilarity = 0.55)
        {
            if (segments == null || lyricSeeds == null || alignments == null || segments.Count == 0)
                return false;

            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                var seed = lyricSeeds[i];
                if (seed.ExpectedStart < window.Start - expectedPaddingSeconds
                    || seed.ExpectedStart > window.End + expectedPaddingSeconds)
                {
                    continue;
                }

                foreach (var segment in segments)
                {
                    var signal = EvaluateSecondaryAlignmentSegment(seed.Text, segment.Text, segment.Start, segment.End);
                    if (signal.FullSimilarity >= minFullSimilarity
                        || signal.PrefixSimilarity >= minPrefixSimilarity
                        || signal.SuffixSimilarity >= minSuffixSimilarity)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static SecondaryAlignmentWindow BuildExpandedFocusedRetryWindow(
            SecondaryAlignmentWindow window,
            double prePaddingSeconds = 1.5,
            double postPaddingSeconds = 2.5)
        {
            return new SecondaryAlignmentWindow(
                Math.Max(0, window.Start - prePaddingSeconds),
                window.End + postPaddingSeconds);
        }

        public static List<LyricTimingAlignment> ApplySecondaryAlignmentStartHints(
            IReadOnlyList<LyricTimingAlignment> alignments,
            IReadOnlyDictionary<int, SecondaryAlignmentSignal>? signals)
        {
            if (alignments == null || alignments.Count == 0)
                return new List<LyricTimingAlignment>();

            if (signals == null || signals.Count == 0)
                return alignments.ToList();

            var results = new List<LyricTimingAlignment>(alignments.Count);
            for (var i = 0; i < alignments.Count; i++)
            {
                var alignment = alignments[i];
                if (!signals.TryGetValue(i, out var signal)
                    || !ShouldUseSecondaryAlignmentHintStart(alignment, signal)
                    || signal.Start >= alignment.End)
                {
                    results.Add(alignment);
                    continue;
                }

                if (alignment.IsMatched)
                {
                    results.Add(alignment with { Start = signal.Start });
                    continue;
                }

                results.Add(alignment with
                {
                    Start = signal.Start,
                    End = Math.Max(signal.End, alignment.End),
                    Score = Math.Max(alignment.Score, signal.FullSimilarity),
                    IsMatched = true,
                });
            }

            return results;
        }

        /// <summary>
        /// 輸出 Whisper words 供除錯
        /// </summary>
        private void LogWhisperWords(List<WhisperWord> words)
        {
            _logger.LogDebug("Whisper words（前 20 個）:");
            foreach (var word in words.Take(20))
            {
                _logger.LogDebug("  {T:F2}s「{W}」", word.Start, word.Word);
            }
        }

        private IReadOnlyList<PhoneticToken> ToPhoneticTokens(string text)
        {
            try
            {
                return _rubyGenerator.Tokenize(CollapseInlineKanaReading(text))
                    .Select(token => new PhoneticToken(token.Surface, token.Reading))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ToPhoneticTokens 失敗，改用原文正規化：{Text}", text);
                return Array.Empty<PhoneticToken>();
            }
        }

        private static string CollapseInlineKanaReading(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var collapsed = Regex.Replace(
                text,
                @"(?<base>[一-龯々ヶ〆ヵ][一-龯々ヶ〆ヵァ-ヶぁ-ん]*)[(（](?<reading>[ぁ-んァ-ンー]+)[)）]",
                match => match.Groups["reading"].Value);

            return ConvertKatakanaToHiragana(collapsed);
        }

        private static string ConvertKatakanaToHiragana(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (ch >= '\u30a1' && ch <= '\u30f6')
                    builder.Append((char)(ch - 0x60));
                else
                    builder.Append(ch);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 正規化日文字串
        /// </summary>
        private static string NormalizeJapanese(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var result = ConvertKatakanaToHiragana(text.Replace(" ", "").Replace("　", ""));
            result = Regex.Replace(result, @"[、。！？「」『』（）\[\]【】・…―ー～〜,\.!?\-]", "");
            result = result.ToLowerInvariant();

            return result;
        }

        /// <summary>
        /// 計算相似度
        /// </summary>
        private static double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
            if (s1 == s2) return 1.0;

            var distance = LevenshteinDistance(s1, s2);
            var maxLen = Math.Max(s1.Length, s2.Length);

            return 1.0 - (double)distance / maxLen;
        }

        private static int LevenshteinDistance(string s1, string s2)
        {
            var n = s1.Length;
            var m = s2.Length;
            var d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (var i = 0; i <= n; i++) d[i, 0] = i;
            for (var j = 0; j <= m; j++) d[0, j] = j;

            for (var i = 1; i <= n; i++)
            {
                for (var j = 1; j <= m; j++)
                {
                    var cost = s2[j - 1] == s1[i - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }

        /// <summary>
        /// 用 ffmpeg 擷取音訊片段
        /// </summary>
        private async Task<string?> TrimAudioAsync(string inputPath, int skipSeconds, int durationSeconds, CancellationToken ct)
        {
            var ffmpegPath = string.IsNullOrWhiteSpace(_options.FfmpegPath)
                ? (_configuration["FFmpegPath"] ?? "ffmpeg")
                : _options.FfmpegPath;
            var outputPath = Path.Combine(Path.GetTempPath(), $"vocal_onset_{Guid.NewGuid()}.mp3");

            var args = skipSeconds > 0
                ? $"-y -ss {skipSeconds} -i \"{inputPath}\" -t {durationSeconds} -acodec libmp3lame -ar 16000 -ac 1 \"{outputPath}\""
                : $"-y -i \"{inputPath}\" -t {durationSeconds} -acodec libmp3lame -ar 16000 -ac 1 \"{outputPath}\"";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                return null;
            }

            return outputPath;
        }

        /// <summary>
        /// 呼叫 Whisper API（啟用 word-level timestamps）
        /// </summary>
        private async Task<TranscriptionAttemptResult> TranscribeWithWordTimestampsAsync(string audioFilePath, CancellationToken ct, string? localModelOverride = null, bool allowOpenAiFallback = true)
        {
            AppendPrecisionAlignmentTrace($"word-transcribe:start audio={audioFilePath}");

            var localAttempt = await TranscribeWithLocalFasterWhisperAsync(audioFilePath, ct, localModelOverride);
            if (localAttempt.Result != null && localAttempt.Result.Words.Count > 0)
            {
                _logger.LogInformation("使用本機 faster-whisper 取得 {Count} 個 words", localAttempt.Result.Words.Count);
                AppendPrecisionAlignmentTrace($"word-transcribe:local-success words={localAttempt.Result.Words.Count}");
                return localAttempt;
            }

            if (!allowOpenAiFallback)
            {
                AppendPrecisionAlignmentTrace($"word-transcribe:openai-disabled reason={localAttempt.FailureReason}; detail={localAttempt.FailureDetail}");
                return localAttempt;
            }

            AppendPrecisionAlignmentTrace($"word-transcribe:local-fallback reason={localAttempt.FailureReason}; detail={localAttempt.FailureDetail}");

            var apiKey = !string.IsNullOrWhiteSpace(_options.OpenAiMyApiKey)
                ? _options.OpenAiMyApiKey
                : (!string.IsNullOrWhiteSpace(_options.OpenAiApiKey)
                    ? _options.OpenAiApiKey
                    : (_configuration["OpenAI:MyApiKey"] ?? _configuration["OpenAI:ApiKey"]));
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("OpenAI API key 未設定");
                AppendPrecisionAlignmentTrace("word-transcribe:openai-skip missing-api-key");
                return new TranscriptionAttemptResult(null, localAttempt.FailureReason ?? "openai_api_key_missing", localAttempt.FailureDetail ?? "OpenAI API key 未設定");
            }

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpClient.Timeout = TimeSpan.FromSeconds(90);

            using var form = new MultipartFormDataContent();
            await using var audioFileStream = File.OpenRead(audioFilePath);
            var audioContent = new StreamContent(audioFileStream);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

            form.Add(audioContent, "file", Path.GetFileName(audioFilePath));
            form.Add(new StringContent("whisper-1"), "model");
            form.Add(new StringContent("ja"), "language");
            form.Add(new StringContent("verbose_json"), "response_format");
            // 啟用 word-level timestamps
            form.Add(new StringContent("word"), "timestamp_granularities[]");

            HttpResponseMessage response;
            try
            {
                AppendPrecisionAlignmentTrace("word-transcribe:openai-start");
                response = await httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", form, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Whisper API 呼叫失敗");
                AppendPrecisionAlignmentTrace($"word-transcribe:openai-exception {ex.Message}");
                return new TranscriptionAttemptResult(null, "openai_http_failed", ex.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Whisper API 錯誤: {Error}", error);
                AppendPrecisionAlignmentTrace($"word-transcribe:openai-non-success {error}");
                return new TranscriptionAttemptResult(null, "openai_non_success_status", error);
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseWhisperWordResponse(jsonResponse);
            if (parsed == null)
            {
                AppendPrecisionAlignmentTrace("word-transcribe:openai-parse-failed");
                return new TranscriptionAttemptResult(null, "openai_parse_failed", jsonResponse.Length > 200 ? jsonResponse[..200] : jsonResponse);
            }

            if (parsed.Words.Count == 0)
            {
                AppendPrecisionAlignmentTrace("word-transcribe:openai-empty-words");
                return new TranscriptionAttemptResult(parsed, "openai_empty_words", "OpenAI 回傳 0 words");
            }

            AppendPrecisionAlignmentTrace($"word-transcribe:openai-success words={parsed.Words.Count}");
            return new TranscriptionAttemptResult(parsed);
        }

        private async Task<TranscriptionAttemptResult> TranscribeWithLocalFasterWhisperAsync(string audioFilePath, CancellationToken ct, string? modelOverride = null)
        {
            try
            {
                var pythonCommand = !string.IsNullOrWhiteSpace(_options.PythonPath)
                    ? _options.PythonPath
                    : ResolvePythonExecutablePath(_configuration);
                var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "faster_whisper_words.py");
                if (!File.Exists(scriptPath))
                {
                    _logger.LogInformation("faster-whisper script 不存在：{Path}", scriptPath);
                    AppendPrecisionAlignmentTrace($"local-faster-whisper:missing-script path={scriptPath}");
                    return new TranscriptionAttemptResult(null, "local_faster_whisper_script_missing", scriptPath);
                }

                var outputJson = Path.Combine(Path.GetTempPath(), $"faster_whisper_words_{Guid.NewGuid()}.json");
                var timeoutSeconds = Math.Max(15, _configuration.GetValue<int?>("LocalFasterWhisperTimeoutSeconds") ?? 600);
                try
                {
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonCommand,
                        Arguments = $"\"{scriptPath}\" \"{audioFilePath}\" \"{outputJson}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    ApplyFasterWhisperEnvironment(process.StartInfo, _options, _configuration, modelOverride);
                    AppendPrecisionAlignmentTrace($"local-faster-whisper:start python={pythonCommand}; script={scriptPath}; audio={audioFilePath}; output={outputJson}; timeout={timeoutSeconds}s");
                    process.Start();

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        await process.WaitForExitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await WaitForProcessExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
                        var timeoutStdout = await ReadProcessOutputWithTimeoutAsync(stdoutTask, TimeSpan.FromSeconds(5));
                        var timeoutStderr = await ReadProcessOutputWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(5));
                        var recoveredResult = await TryRecoverLocalFasterWhisperOutputJsonAsync(outputJson);
                        if (recoveredResult != null)
                        {
                            AppendPrecisionAlignmentTrace($"local-faster-whisper:timeout-recovered-json words={recoveredResult.Words.Count} stdout={timeoutStdout} stderr={timeoutStderr}");
                            return new TranscriptionAttemptResult(recoveredResult);
                        }

                        AppendPrecisionAlignmentTrace($"local-faster-whisper:timeout after={timeoutSeconds}s stdout={timeoutStdout} stderr={timeoutStderr}");
                        return new TranscriptionAttemptResult(null, "local_faster_whisper_timeout", $"timeout={timeoutSeconds}s; stdout={timeoutStdout}; stderr={timeoutStderr}");
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await WaitForProcessExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
                        AppendPrecisionAlignmentTrace("local-faster-whisper:cancelled-by-request");
                        throw;
                    }

                    var stdout = await ReadProcessOutputWithTimeoutAsync(stdoutTask, TimeSpan.FromSeconds(30));
                    var stderr = await ReadProcessOutputWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(30));

                    if (process.ExitCode != 0)
                    {
                        _logger.LogWarning("faster-whisper script 失敗 (python={Python}, exit {Code}) stdout={Stdout} stderr={Stderr}", pythonCommand, process.ExitCode, stdout, stderr);
                        AppendPrecisionAlignmentTrace($"local-faster-whisper:non-zero exit={process.ExitCode} stdout={stdout} stderr={stderr}");
                        return new TranscriptionAttemptResult(null, "local_faster_whisper_process_failed", $"python={pythonCommand}; exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
                    }

                    if (!File.Exists(outputJson))
                    {
                        _logger.LogWarning("faster-whisper script 未輸出 JSON：{Path}", outputJson);
                        AppendPrecisionAlignmentTrace($"local-faster-whisper:no-json output={outputJson}; stdout={stdout}; stderr={stderr}");
                        return new TranscriptionAttemptResult(null, "local_faster_whisper_no_json", outputJson);
                    }

                    var jsonResponse = await File.ReadAllTextAsync(outputJson, ct);
                    var parsed = ParseWhisperWordResponse(jsonResponse);
                    if (parsed == null)
                    {
                        AppendPrecisionAlignmentTrace("local-faster-whisper:parse-failed");
                        return new TranscriptionAttemptResult(null, "local_faster_whisper_parse_failed", jsonResponse.Length > 200 ? jsonResponse[..200] : jsonResponse);
                    }

                    if (parsed.Words.Count == 0)
                    {
                        AppendPrecisionAlignmentTrace("local-faster-whisper:empty-words");
                        return new TranscriptionAttemptResult(parsed, "local_faster_whisper_empty_words", "local faster-whisper 回傳 0 words");
                    }

                    AppendPrecisionAlignmentTrace($"local-faster-whisper:success words={parsed.Words.Count}");
                    return new TranscriptionAttemptResult(parsed);
                }
                finally
                {
                    CleanupFile(outputJson);
                }
            }
            catch (OperationCanceledException)
            {
                AppendPrecisionAlignmentTrace("local-faster-whisper:cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TranscribeWithLocalFasterWhisperAsync 失敗");
                AppendPrecisionAlignmentTrace($"local-faster-whisper:exception {ex.Message}");
                return new TranscriptionAttemptResult(null, "local_faster_whisper_exception", ex.Message);
            }
        }

        private async Task<WhisperResult?> TryRecoverLocalFasterWhisperOutputJsonAsync(string outputJson)
        {
            try
            {
                if (!File.Exists(outputJson))
                    return null;

                var jsonResponse = await File.ReadAllTextAsync(outputJson, CancellationToken.None);
                var parsed = ParseWhisperWordResponse(jsonResponse);
                if (parsed?.Words.Count > 0)
                    return parsed;

                AppendPrecisionAlignmentTrace("local-faster-whisper:timeout-recovered-json-empty");
                return null;
            }
            catch (Exception ex)
            {
                AppendPrecisionAlignmentTrace($"local-faster-whisper:timeout-recovered-json-failed {ex.Message}");
                return null;
            }
        }

        private static async Task WaitForProcessExitAfterKillAsync(Process process, TimeSpan timeout)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
            }
        }

        private static async Task<string> ReadProcessOutputWithTimeoutAsync(Task<string> outputTask, TimeSpan timeout)
        {
            try
            {
                return await outputTask.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                return $"[process output read timed out after {timeout.TotalSeconds:0}s]";
            }
            catch (InvalidOperationException ex)
            {
                return $"[process output unavailable: {ex.Message}]";
            }
        }

        private async Task<List<SecondaryAlignmentSegment>> TryTranscribeSecondarySegmentsAsync(
            string audioFilePath,
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            CancellationToken ct)
        {
            var modelName = !string.IsNullOrWhiteSpace(_options.SecondaryAlignmentModel)
                ? _options.SecondaryAlignmentModel
                : (_configuration["SecondaryAlignmentModel"] ?? Environment.GetEnvironmentVariable("LEARNMORE_SECONDARY_ALIGNMENT_MODEL"));
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _logger.LogInformation("second-opinion transcription 停用：未設定 SecondaryAlignmentModel");
                AppendSecondaryAlignmentTrace("disabled: SecondaryAlignmentModel not set");
                return new List<SecondaryAlignmentSegment>();
            }

            var pythonCommand = !string.IsNullOrWhiteSpace(_options.SecondaryAlignmentPythonPath)
                ? _options.SecondaryAlignmentPythonPath
                : _configuration["SecondaryAlignmentPythonPath"];
            if (string.IsNullOrWhiteSpace(pythonCommand))
                pythonCommand = ResolvePythonExecutablePath(_configuration);
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "openai_whisper_segments.py");
            if (!File.Exists(scriptPath))
            {
                _logger.LogWarning("second-opinion script 不存在：{Path}", scriptPath);
                AppendSecondaryAlignmentTrace($"missing-script: {scriptPath}");
                return new List<SecondaryAlignmentSegment>();
            }

            var windows = BuildSecondaryAlignmentWindows(lyricSeeds, alignments);
            var focusedWindows = BuildFocusedSecondaryAlignmentWindows(lyricSeeds, alignments);
            var targetIndexes = GetSecondaryAlignmentTargetLineIndexes(lyricSeeds, alignments);
            AppendSecondaryAlignmentTrace($"window-counts: broad={windows.Count}; focused={focusedWindows.Count}; targetLines={string.Join(',', targetIndexes)}");
            if (focusedWindows.Count == 0)
            {
                var focusedCandidates = Enumerable.Range(0, Math.Min(lyricSeeds.Count, alignments.Count))
                    .Where(i => !alignments[i].IsMatched)
                    .Select(i => $"line[{i}] score={alignments[i].Score:F3} len={NormalizeJapanese(lyricSeeds[i].Text).Length} text={lyricSeeds[i].Text}");
                foreach (var candidate in focusedCandidates)
                    AppendSecondaryAlignmentTrace($"focused-candidate: {candidate}");
            }
            if (!HasSecondaryAlignmentWork(lyricSeeds, alignments))
            {
                AppendSecondaryAlignmentTrace("no-target-windows: keep original alignments");
                AppendSecondaryAlignmentTrace($"window-diagnostic-count: lyricSeeds={lyricSeeds.Count}; alignments={alignments.Count}");
                foreach (var diagnostic in BuildSecondaryAlignmentWindowDiagnostics(lyricSeeds, alignments))
                    AppendSecondaryAlignmentTrace($"window-diagnostic: {diagnostic}");
                return new List<SecondaryAlignmentSegment>();
            }

            AppendSecondaryAlignmentTrace($"target-lines: {string.Join(',', targetIndexes)}");
            var diagnosticLines = BuildSecondaryAlignmentWindowDiagnostics(lyricSeeds, alignments).ToList();
            foreach (var diagnostic in diagnosticLines
                .Where(line => targetIndexes.Any(i => line.StartsWith($"line[{i}]"))))
            {
                AppendSecondaryAlignmentTrace($"target-diagnostic: {diagnostic}");
            }

            var contextIndexes = targetIndexes
                .SelectMany(i => Enumerable.Range(Math.Max(0, i - 2), Math.Min(Math.Max(0, lyricSeeds.Count - Math.Max(0, i - 2)), 5)))
                .Distinct()
                .OrderBy(i => i)
                .ToHashSet();
            foreach (var diagnostic in diagnosticLines
                .Where(line => contextIndexes.Any(i => line.StartsWith($"line[{i}]"))))
            {
                AppendSecondaryAlignmentTrace($"context-diagnostic: {diagnostic}");
            }

            _logger.LogInformation(
                "second-opinion transcription 啟動 (python={Python}, model={Model}, script={Script}, audio={Audio}, windows={WindowCount})",
                pythonCommand,
                modelName,
                scriptPath,
                audioFilePath,
                windows.Count);
            AppendSecondaryAlignmentTrace($"start: python={pythonCommand}; model={modelName}; script={scriptPath}; audio={audioFilePath}; windows={string.Join(",", windows.Select(w => $"{w.Start:F2}-{w.End:F2}"))}");
            if (focusedWindows.Count > 0)
            {
                AppendSecondaryAlignmentTrace($"focused-windows: {string.Join(",", focusedWindows.Select(w => $"{w.Start:F2}-{w.End:F2}"))}; model=small");
            }

            var aggregated = new List<SecondaryAlignmentSegment>();

            async Task<(List<SecondaryAlignmentSegment> Segments, int JsonLength)> RunWindowAsync(SecondaryAlignmentWindow attemptWindow, string attemptModelName)
            {
                var outputJson = Path.Combine(Path.GetTempPath(), $"openai_whisper_segments_{Guid.NewGuid()}.json");
                var timeoutSeconds = Math.Max(15, _configuration.GetValue<int?>("SecondaryAlignmentWindowTimeoutSeconds") ?? 120);
                try
                {
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonCommand,
                        Arguments = $"\"{scriptPath}\" \"{audioFilePath}\" \"{outputJson}\" \"{attemptModelName}\" {attemptWindow.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)} {attemptWindow.End.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    ApplyFfmpegEnvironment(process.StartInfo, _configuration);
                    process.Start();

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                    try
                    {
                        await process.WaitForExitAsync(linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await WaitForProcessExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
                        var timeoutStdout = await ReadProcessOutputWithTimeoutAsync(stdoutTask, TimeSpan.FromSeconds(5));
                        var timeoutStderr = await ReadProcessOutputWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(5));
                        AppendSecondaryAlignmentTrace($"timeout: model={attemptModelName}; window={attemptWindow.Start:F2}-{attemptWindow.End:F2}; after={timeoutSeconds}s; stdout={timeoutStdout}; stderr={timeoutStderr}");
                        return (new List<SecondaryAlignmentSegment>(), 0);
                    }
                    catch (OperationCanceledException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await WaitForProcessExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
                        throw;
                    }

                    var stdout = await ReadProcessOutputWithTimeoutAsync(stdoutTask, TimeSpan.FromSeconds(30));
                    var stderr = await ReadProcessOutputWithTimeoutAsync(stderrTask, TimeSpan.FromSeconds(30));

                    if (process.ExitCode != 0 || !File.Exists(outputJson))
                    {
                        _logger.LogInformation("second-opinion transcription 跳過 (python={Python}, model={Model}, window={Window}, exit={Code}) stdout={Stdout} stderr={Stderr}", pythonCommand, attemptModelName, $"{attemptWindow.Start:F2}-{attemptWindow.End:F2}", process.ExitCode, stdout, stderr);
                        AppendSecondaryAlignmentTrace($"skip: model={attemptModelName}; window={attemptWindow.Start:F2}-{attemptWindow.End:F2}; exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
                        return (new List<SecondaryAlignmentSegment>(), 0);
                    }

                    var json = await File.ReadAllTextAsync(outputJson, ct);
                    var parsed = ParseSecondaryAlignmentSegments(json);
                    _logger.LogInformation("second-opinion transcription 取得 {Count} 個 segments (model={Model}, window={Window})", parsed.Count, attemptModelName, $"{attemptWindow.Start:F2}-{attemptWindow.End:F2}");
                    AppendSecondaryAlignmentTrace($"segments: model={attemptModelName}; window={attemptWindow.Start:F2}-{attemptWindow.End:F2}; count={parsed.Count}; jsonLength={json.Length}");
                    return (parsed, json.Length);
                }
                finally
                {
                    CleanupFile(outputJson);
                }
            }

            foreach (var window in windows)
            {
                try
                {
                    var primary = await RunWindowAsync(window, modelName);
                    var parsed = primary.Segments;
                    if (parsed.Count == 0)
                    {
                        var retryWindow = new SecondaryAlignmentWindow(
                            Math.Max(0, window.Start - 1.5),
                            window.End + 2.5);
                        if (Math.Abs(retryWindow.Start - window.Start) > 0.01 || Math.Abs(retryWindow.End - window.End) > 0.01)
                        {
                            var retryModelName = string.Equals(modelName, "tiny", StringComparison.OrdinalIgnoreCase)
                                ? "small"
                                : modelName;
                            AppendSecondaryAlignmentTrace($"retry-expanded-window: from={window.Start:F2}-{window.End:F2}; to={retryWindow.Start:F2}-{retryWindow.End:F2}; model={retryModelName}");
                            var retry = await RunWindowAsync(retryWindow, retryModelName);
                            if (retry.Segments.Count > 0)
                            {
                                AppendSecondaryAlignmentTrace($"retry-expanded-window: recovered count={retry.Segments.Count}; model={retryModelName}");
                                parsed = retry.Segments;
                            }
                        }
                    }

                    aggregated.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "second-opinion transcription 失敗，保留原始對齊結果");
                    AppendSecondaryAlignmentTrace($"exception: window={window.Start:F2}-{window.End:F2}; {ex}");
                    return new List<SecondaryAlignmentSegment>();
                }
            }

            foreach (var window in focusedWindows)
            {
                try
                {
                    var focused = await RunWindowAsync(window, "small");
                    var focusedSegments = focused.Segments;
                    var hasViableFocusedCandidate = HasViableFocusedSecondaryAlignmentCandidate(window, focusedSegments, lyricSeeds, alignments);
                    if (!hasViableFocusedCandidate && focusedSegments.Count > 0)
                    {
                        AppendSecondaryAlignmentTrace($"focused-window: reject non-lyric-like segments; window={window.Start:F2}-{window.End:F2}; count={focusedSegments.Count}");
                    }

                    if (!hasViableFocusedCandidate)
                    {
                        var retryWindow = BuildExpandedFocusedRetryWindow(window);
                        if (Math.Abs(retryWindow.Start - window.Start) > 0.01 || Math.Abs(retryWindow.End - window.End) > 0.01)
                        {
                            AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: from={window.Start:F2}-{window.End:F2}; to={retryWindow.Start:F2}-{retryWindow.End:F2}; model=small");
                            var retry = await RunWindowAsync(retryWindow, "small");
                            if (HasViableFocusedSecondaryAlignmentCandidate(retryWindow, retry.Segments, lyricSeeds, alignments))
                            {
                                AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: recovered count={retry.Segments.Count}; window={retryWindow.Start:F2}-{retryWindow.End:F2}; model=small");
                                focusedSegments = retry.Segments;
                                hasViableFocusedCandidate = true;
                            }
                            else
                            {
                                if (retry.Segments.Count > 0)
                                {
                                    AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: reject non-lyric-like retry segments; window={retryWindow.Start:F2}-{retryWindow.End:F2}; count={retry.Segments.Count}");
                                }

                                if (!string.Equals(modelName, "small", StringComparison.OrdinalIgnoreCase))
                                {
                                    AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: fallback model retry={modelName}; window={retryWindow.Start:F2}-{retryWindow.End:F2}");
                                    var modelRetry = await RunWindowAsync(retryWindow, modelName);
                                    if (HasViableFocusedSecondaryAlignmentCandidate(retryWindow, modelRetry.Segments, lyricSeeds, alignments))
                                    {
                                        AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: recovered count={modelRetry.Segments.Count}; window={retryWindow.Start:F2}-{retryWindow.End:F2}; model={modelName}");
                                        focusedSegments = modelRetry.Segments;
                                        hasViableFocusedCandidate = true;
                                    }
                                    else if (modelRetry.Segments.Count > 0)
                                    {
                                        AppendSecondaryAlignmentTrace($"focused-retry-expanded-window: reject non-lyric-like retry segments; window={retryWindow.Start:F2}-{retryWindow.End:F2}; count={modelRetry.Segments.Count}; model={modelName}");
                                    }
                                }
                            }
                        }
                    }

                    if (hasViableFocusedCandidate && focusedSegments.Count > 0)
                    {
                        AppendSecondaryAlignmentTrace($"focused-window: recovered count={focusedSegments.Count}; window={window.Start:F2}-{window.End:F2}; model=small");
                        aggregated.AddRange(focusedSegments);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "focused second-opinion transcription 失敗，略過窄窗補探");
                    AppendSecondaryAlignmentTrace($"focused-exception: window={window.Start:F2}-{window.End:F2}; {ex}");
                }
            }

            foreach (var sample in aggregated.OrderBy(s => s.Start).Take(5))
            {
                _logger.LogInformation(
                    "second-opinion sample segment {Start:F2}-{End:F2}: {Text}",
                    sample.Start,
                    sample.End,
                    sample.Text);
                AppendSecondaryAlignmentTrace($"sample: {sample.Start:F2}-{sample.End:F2} {sample.Text}");
            }

            return aggregated;
        }

        private async Task<List<LyricTimingAlignment>> TryApplySecondaryAlignmentHintsAsync(
            string audioFilePath,
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            CancellationToken ct)
        {
            var segments = await TryTranscribeSecondarySegmentsAsync(audioFilePath, lyricSeeds, alignments, ct);
            if (segments.Count == 0)
            {
                _logger.LogInformation("second-opinion 未提供任何 segments，保留原始對齊結果");
                AppendSecondaryAlignmentTrace("no-segments: keep original alignments");
                return alignments.ToList();
            }

            var hints = BuildSecondaryAlignmentHints(lyricSeeds, alignments, segments);
            if (hints.Count == 0)
            {
                var targetIndexes = GetSecondaryAlignmentTargetLineIndexes(lyricSeeds, alignments);
                AppendSecondaryAlignmentTrace($"hints: count=0; targetLines={string.Join(',', targetIndexes)}");
                foreach (var diagnostic in BuildSecondaryAlignmentDiagnostics(lyricSeeds, alignments, segments, targetIndexes))
                {
                    _logger.LogInformation("second-opinion 診斷：{Diagnostic}", diagnostic);
                    AppendSecondaryAlignmentTrace($"diagnostic: {diagnostic}");
                }
                return alignments.ToList();
            }

            _logger.LogInformation("second-opinion 對 {Count} 句提供起點修正 hint", hints.Count);
            AppendSecondaryAlignmentTrace($"hints: count={hints.Count}");
            var updatedAlignments = ApplySecondaryAlignmentStartHints(alignments, hints);
            var unmatchedIndexes = updatedAlignments
                .Select((alignment, index) => new { alignment.IsMatched, index })
                .Where(item => !item.IsMatched)
                .Select(item => item.index)
                .ToList();
            foreach (var diagnostic in BuildSecondaryAlignmentDiagnostics(lyricSeeds, updatedAlignments, segments, unmatchedIndexes))
            {
                _logger.LogInformation("second-opinion 未命中診斷：{Diagnostic}", diagnostic);
                AppendSecondaryAlignmentTrace($"post-hint-diagnostic: {diagnostic}");
            }
            return updatedAlignments;
        }

        private static void AppendSecondaryAlignmentTrace(string message)
        {
            try
            {
                File.AppendAllText(
                    SecondaryAlignmentTracePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static void AppendPrecisionAlignmentTrace(string message)
        {
            try
            {
                File.AppendAllText(
                    PrecisionAlignmentTracePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private static void ApplyFfmpegEnvironment(ProcessStartInfo startInfo, IConfiguration configuration)
        {
            if (startInfo == null)
                return;

            var ffmpegPath = configuration["FFmpegPath"];
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return;

            var ffmpegDir = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrWhiteSpace(ffmpegDir) || !Directory.Exists(ffmpegDir))
                return;

            var currentPath = (startInfo.Environment.TryGetValue("PATH", out var existingPath)
                ? existingPath
                : Environment.GetEnvironmentVariable("PATH")) ?? string.Empty;

            if (currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(path => string.Equals(path.Trim(), ffmpegDir, StringComparison.OrdinalIgnoreCase)))
                return;

            startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                ? ffmpegDir
                : ffmpegDir + Path.PathSeparator + currentPath;
        }

        private static IEnumerable<string> BuildSecondaryAlignmentDiagnostics(
            IReadOnlyList<LyricTimingSeed> lyricSeeds,
            IReadOnlyList<LyricTimingAlignment> alignments,
            IReadOnlyList<SecondaryAlignmentSegment> segments,
            IReadOnlyCollection<int>? lineIndexes = null,
            double expectedSearchPaddingSeconds = 8)
        {
            var diagnostics = new List<string>();
            if (lyricSeeds == null || alignments == null || segments == null)
                return diagnostics;

            var count = Math.Min(lyricSeeds.Count, alignments.Count);
            for (var i = 0; i < count; i++)
            {
                if (lineIndexes != null && lineIndexes.Count > 0 && !lineIndexes.Contains(i))
                    continue;

                var seed = lyricSeeds[i];
                var alignment = alignments[i];

                SecondaryAlignmentSignal? bestSignal = null;
                foreach (var signal in EnumerateSecondaryAlignmentSignals(seed.Text, segments, seed.ExpectedStart, alignment.End, expectedSearchPaddingSeconds))
                {
                    if (bestSignal == null
                        || signal.FullSimilarity > bestSignal.FullSimilarity
                        || (Math.Abs(signal.FullSimilarity - bestSignal.FullSimilarity) < 0.0001 && signal.SuffixSimilarity > bestSignal.SuffixSimilarity)
                        || (Math.Abs(signal.FullSimilarity - bestSignal.FullSimilarity) < 0.0001 && Math.Abs(signal.SuffixSimilarity - bestSignal.SuffixSimilarity) < 0.0001 && signal.PrefixSimilarity > bestSignal.PrefixSimilarity))
                    {
                        bestSignal = signal;
                    }
                }

                if (bestSignal == null)
                {
                    var currentLabel = alignment.IsMatched ? alignment.Start.ToString("F2") : "unmatched";
                    diagnostics.Add($"line[{i}] 無候選 segments: current={currentLabel} text={seed.Text}");
                    continue;
                }

                var currentDisplay = alignment.IsMatched ? alignment.Start.ToString("F2") : "unmatched";
                diagnostics.Add(
                    $"line[{i}] current={currentDisplay} candidate={bestSignal.Start:F2}-{bestSignal.End:F2} full={bestSignal.FullSimilarity:F3} prefix={bestSignal.PrefixSimilarity:F3} suffix={bestSignal.SuffixSimilarity:F3} anchorFull={bestSignal.AnchorFullSimilarity:F3} anchorSuffix={bestSignal.AnchorSuffixSimilarity:F3} segments={bestSignal.SegmentCount} use={ShouldUseSecondaryAlignmentHintStart(alignment, bestSignal)} lyric={seed.Text} segment={bestSignal.Text}");
            }

            return diagnostics;
        }

        private static IEnumerable<SecondaryAlignmentSignal> EnumerateSecondaryAlignmentSignals(
            string lyricText,
            IReadOnlyList<SecondaryAlignmentSegment> segments,
            double expectedStart,
            double currentEnd,
            double expectedSearchPaddingSeconds,
            int maxSegmentsToCombine = 3,
            double maxGapSeconds = 0.35)
        {
            if (segments == null || segments.Count == 0)
                yield break;

            for (var i = 0; i < segments.Count; i++)
            {
                var first = segments[i];
                if (first.End < expectedStart - expectedSearchPaddingSeconds)
                    continue;
                if (first.Start > currentEnd + expectedSearchPaddingSeconds)
                    continue;

                var combinedText = first.Text;
                var combinedStart = first.Start;
                var combinedEnd = first.End;
                yield return EvaluateSecondaryAlignmentSegment(lyricText, combinedText, combinedStart, combinedEnd);

                for (var j = i + 1; j < segments.Count && j < i + maxSegmentsToCombine; j++)
                {
                    var next = segments[j];
                    if ((next.Start - combinedEnd) > maxGapSeconds)
                        break;
                    if (next.Start > currentEnd + expectedSearchPaddingSeconds)
                        break;

                    combinedText = string.Concat(combinedText, next.Text);
                    combinedEnd = next.End;
                    var combinedSignal = EvaluateSecondaryAlignmentSegment(lyricText, combinedText, combinedStart, combinedEnd);
                    var anchorSignal = EvaluateSecondaryAlignmentSegment(lyricText, next.Text, next.Start, next.End);
                    yield return combinedSignal with
                    {
                        SegmentCount = j - i + 1,
                        AnchorFullSimilarity = anchorSignal.FullSimilarity,
                        AnchorPrefixSimilarity = anchorSignal.PrefixSimilarity,
                        AnchorSuffixSimilarity = anchorSignal.SuffixSimilarity,
                    };
                }
            }
        }

        private List<SecondaryAlignmentSegment> ParseSecondaryAlignmentSegments(string jsonResponse)
        {
            try
            {
                var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;
                if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
                    return new List<SecondaryAlignmentSegment>();

                var results = new List<SecondaryAlignmentSegment>();
                foreach (var seg in segmentsElement.EnumerateArray())
                {
                    var text = seg.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var start = seg.TryGetProperty("start", out var startProp) ? startProp.GetDouble() : 0;
                    var end = seg.TryGetProperty("end", out var endProp) ? endProp.GetDouble() : 0;
                    results.Add(new SecondaryAlignmentSegment(start, end, text));
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "解析 second-opinion segments 失敗");
                return new List<SecondaryAlignmentSegment>();
            }
        }

        private WhisperResult? ParseWhisperWordResponse(string jsonResponse)
        {
            try
            {
                var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                var result = new WhisperResult();

                // 解析 words
                if (root.TryGetProperty("words", out var wordsElement) &&
                    wordsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var wordEl in wordsElement.EnumerateArray())
                    {
                        var word = wordEl.TryGetProperty("word", out var wProp) ? wProp.GetString() : "";
                        var start = wordEl.TryGetProperty("start", out var sProp) ? sProp.GetDouble() : 0;
                        var end = wordEl.TryGetProperty("end", out var eProp) ? eProp.GetDouble() : 0;

                        if (!string.IsNullOrEmpty(word))
                        {
                            result.Words.Add(new WhisperWord { Word = word, Start = start, End = end });
                        }
                    }
                }

                // 也解析 segments 作為備用
                if (root.TryGetProperty("segments", out var segmentsElement) &&
                    segmentsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var seg in segmentsElement.EnumerateArray())
                    {
                        // 檢查 segment 內是否有 words
                        if (seg.TryGetProperty("words", out var segWordsEl) &&
                            segWordsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var wordEl in segWordsEl.EnumerateArray())
                            {
                                var word = wordEl.TryGetProperty("word", out var wProp) ? wProp.GetString() : "";
                                var start = wordEl.TryGetProperty("start", out var sProp) ? sProp.GetDouble() : 0;
                                var end = wordEl.TryGetProperty("end", out var eProp) ? eProp.GetDouble() : 0;

                                if (!string.IsNullOrEmpty(word) &&
                                    !result.Words.Any(w => Math.Abs(w.Start - start) < 0.01))
                                {
                                    result.Words.Add(new WhisperWord { Word = word, Start = start, End = end });
                                }
                            }
                        }
                    }
                }

                _logger.LogInformation("Whisper 回傳 {Count} 個 words", result.Words.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析 Whisper 回應失敗");
                return null;
            }
        }

        private void CleanupFile(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }

        private class WhisperResult
        {
            public List<WhisperWord> Words { get; set; } = new();
        }

        private class WhisperWord
        {
            public string Word { get; set; } = "";
            public double Start { get; set; }
            public double End { get; set; }
        }
    }
}
