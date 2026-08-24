using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace LearnMore.Services;

public class WhisperTranslationSourceService : IWhisperTranslationSourceService
{
    private readonly MarumaruCrawlerService _marumaruCrawlerService;
    private readonly IOpenAiWhisperClientService? _openAiWhisperClient;
    private readonly ILogger<WhisperTranslationSourceService> _logger;
    private readonly bool _enableRuntimeOpenAiTranslation;

    public WhisperTranslationSourceService(
        MarumaruCrawlerService marumaruCrawlerService,
        ILogger<WhisperTranslationSourceService> logger,
        IOpenAiWhisperClientService? openAiWhisperClient = null,
        IOptions<WhisperRuntimeOptions>? options = null)
    {
        _marumaruCrawlerService = marumaruCrawlerService;
        _openAiWhisperClient = openAiWhisperClient;
        _logger = logger;
        _enableRuntimeOpenAiTranslation = options?.Value.EnableRuntimeOpenAiTranslation ?? false;
    }

    public async Task<List<LyricSegment>?> TryPreAlignAsync(
        string title,
        string artist,
        IReadOnlyList<LyricSegment> timestampSegments,
        CancellationToken cancellationToken = default,
        bool preferMarumaruLineCount = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(artist)
            || (!preferMarumaruLineCount && timestampSegments.Count == 0))
        {
            return null;
        }

        try
        {
            var marumaruTask = _marumaruCrawlerService.SearchAndFetchAsync(title, artist);
            var completed = await Task.WhenAny(marumaruTask, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed != marumaruTask || marumaruTask.IsFaulted || marumaruTask.IsCanceled)
            {
                return null;
            }

            var marumaruLyrics = await marumaruTask;
            if (marumaruLyrics == null || marumaruLyrics.Count == 0)
            {
                return null;
            }

            _logger.LogInformation("Step 1.5: marumaru 取得 {Count} 行", marumaruLyrics.Count);
            if (preferMarumaruLineCount || ShouldPreferFormalLyricLineCount(timestampSegments.Count, marumaruLyrics.Count))
            {
                if (!preferMarumaruLineCount)
                {
                    _logger.LogInformation(
                        "Step 1.5: 同步來源 {TimestampCount} 行少於正式歌詞 {FormalCount} 行，改以正式歌詞完整行數為主",
                        timestampSegments.Count,
                        marumaruLyrics.Count);
                }

                return _marumaruCrawlerService.AlignLyricsWithTimestamps(
                    marumaruLyrics,
                    timestampSegments.ToList());
            }

            var lrcTimestamps = timestampSegments.Select(s => (s.TimeStamp, s.Japanese ?? string.Empty)).ToList();
            return _marumaruCrawlerService.AlignWithLrc(marumaruLyrics, lrcTimestamps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Step 1.5: marumaru 預比對失敗");
            return null;
        }
    }

    public static bool ShouldPreferFormalLyricLineCount(int timestampLineCount, int formalLyricLineCount)
    {
        if (formalLyricLineCount <= 0)
        {
            return false;
        }

        if (timestampLineCount <= 0)
        {
            return true;
        }

        if (formalLyricLineCount < 8)
        {
            return false;
        }

        if (formalLyricLineCount >= timestampLineCount + 4
            && formalLyricLineCount >= (int)Math.Ceiling(timestampLineCount * 1.2))
        {
            return true;
        }

        return timestampLineCount < (int)Math.Ceiling(formalLyricLineCount * 0.75);
    }

    public async Task<TranslationSourceResolutionResult> ResolveFinalSegmentsAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, List<LyricSegment>? preAlignedSegments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (preAlignedSegments != null
            && preAlignedSegments.Count == stableSegmentsToInsert.Count)
        {
            var completedPreAligned = await FillMissingTranslationsWithGptAsync(
                ClearSuspiciousDuplicateTranslations(preAlignedSegments),
                cancellationToken);
            if (HasCompleteChineseTranslations(completedPreAligned))
            {
                _logger.LogInformation("Step 3: 使用 Step 1.5 預比對的 marumaru 翻譯");
                return new TranslationSourceResolutionResult(completedPreAligned, TranslationSourceKind.PreAligned);
            }
        }

        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
        {
            var marumaruResolved = await TryResolveMarumaruAsync(title, artist, stableSegmentsToInsert, cancellationToken);
            if (marumaruResolved != null)
            {
                return new TranslationSourceResolutionResult(marumaruResolved, TranslationSourceKind.Marumaru);
            }

            var bahaResolved = await TryResolveBahaAsync(title, artist, stableSegmentsToInsert, cancellationToken);
            if (bahaResolved != null)
            {
                return new TranslationSourceResolutionResult(bahaResolved, TranslationSourceKind.Baha);
            }
        }

        var gptResolved = await TryResolveGptAsync(stableSegmentsToInsert, cancellationToken);
        if (gptResolved != null)
        {
            return new TranslationSourceResolutionResult(gptResolved, TranslationSourceKind.Gpt);
        }

        _logger.LogInformation("marumaru/巴哈均未找到翻譯，保留「翻譯中...」");
        return new TranslationSourceResolutionResult(
            stableSegmentsToInsert.Select(seg => new LyricSegment
            {
                TimeStamp = seg.TimeStamp,
                Japanese = seg.Japanese,
                Chinese = string.IsNullOrWhiteSpace(seg.Chinese) ? "翻譯中..." : seg.Chinese
            }).ToList(),
            TranslationSourceKind.Fallback);
    }

    private async Task<List<LyricSegment>?> TryResolveMarumaruAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var marumaruTask = _marumaruCrawlerService.SearchAndFetchAsync(title, artist);
            var completed = await Task.WhenAny(marumaruTask, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed != marumaruTask || marumaruTask.IsFaulted || marumaruTask.IsCanceled)
            {
                _logger.LogWarning("marumaru timed out or faulted");
                return null;
            }

            var marumaruLyrics = await marumaruTask;
            if (marumaruLyrics == null || marumaruLyrics.Count == 0)
            {
                return null;
            }

            _logger.LogInformation("翻譯來源：marumaru（{Count} 行）", marumaruLyrics.Count);
            var lrcTimestamps = stableSegmentsToInsert.Select(s => (s.TimeStamp, s.Japanese ?? string.Empty)).ToList();
            _logger.LogInformation("LRC 時間戳：{Count} 行，與 marumaru {Maru} 行對齊", lrcTimestamps.Count, marumaruLyrics.Count);
            var aligned = _marumaruCrawlerService.AlignWithLrc(marumaruLyrics, lrcTimestamps);
            var filled = await FillMissingTranslationsWithGptAsync(ClearSuspiciousDuplicateTranslations(aligned), cancellationToken);
            if (HasCompleteChineseTranslations(filled))
            {
                return filled;
            }

            _logger.LogWarning("marumaru 翻譯仍有缺漏，改用下一個翻譯來源");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "marumaru SearchAndFetchAsync failed");
            return null;
        }
    }

    private async Task<List<LyricSegment>?> TryResolveBahaAsync(string title, string artist, IReadOnlyList<LyricSegment> stableSegmentsToInsert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bahaTask = _marumaruCrawlerService.SearchBahaLyricsAsync(title, artist, stableSegmentsToInsert.Count);
            var completed = await Task.WhenAny(bahaTask, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed != bahaTask || bahaTask.IsFaulted || bahaTask.IsCanceled)
            {
                _logger.LogWarning("巴哈姆特 timed out or faulted");
                return null;
            }

            var bahaLines = await bahaTask;
            if (bahaLines == null || bahaLines.Count == 0)
            {
                return null;
            }

            _logger.LogInformation("翻譯來源：巴哈姆特（{Count} 行）", bahaLines.Count);
            var resolved = stableSegmentsToInsert.Select((seg, i) => new LyricSegment
            {
                TimeStamp = seg.TimeStamp,
                Japanese = seg.Japanese,
                Chinese = i < bahaLines.Count ? bahaLines[i] : (seg.Chinese ?? "翻譯中...")
            }).ToList();

            if (HasCompleteChineseTranslations(resolved))
            {
                return resolved;
            }

            _logger.LogWarning("巴哈姆特翻譯仍有缺漏，改用下一個翻譯來源");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchBahaLyricsAsync failed");
            return null;
        }
    }

    private async Task<List<LyricSegment>> FillMissingTranslationsWithGptAsync(IReadOnlyList<LyricSegment> segments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completed = segments.Select(segment => new LyricSegment
        {
            LyricID = segment.LyricID,
            TimeStamp = segment.TimeStamp,
            Japanese = segment.Japanese,
            Chinese = segment.Chinese,
            JapaneseRuby = segment.JapaneseRuby,
            Roman = segment.Roman
        }).ToList();

        if (_openAiWhisperClient == null || !_enableRuntimeOpenAiTranslation)
        {
            return completed;
        }

        var missing = completed
            .Select((segment, index) => (segment, index))
            .Where(item =>
                !HasUsableChineseTranslation(item.segment.Chinese)
                && !string.IsNullOrWhiteSpace(item.segment.Japanese))
            .ToList();

        if (missing.Count == 0)
        {
            return completed;
        }

        const int batchSize = 20;
        for (var offset = 0; offset < missing.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = missing.Skip(offset).Take(batchSize).ToList();
            var combinedJapanese = string.Join("@", batch.Select(item => item.segment.Japanese.Trim()));
            var translated = await _openAiWhisperClient.BatchTranslateToChineseAsync(combinedJapanese);
            if (string.IsNullOrWhiteSpace(translated))
            {
                _logger.LogWarning("GPT 補翻譯批次回傳空白，offset={Offset}, count={Count}", offset, batch.Count);
                await FillMissingBatchLineByLineAsync(completed, batch, cancellationToken);
                continue;
            }

            var translatedLines = translated
                .Split('@')
                .Select(line => line.Trim())
                .ToList();

            if (translatedLines.Count != batch.Count || translatedLines.Any(string.IsNullOrWhiteSpace))
            {
                _logger.LogWarning("GPT 補翻譯批次行數不符或含空白，offset={Offset}, expected={Expected}, actual={Actual}", offset, batch.Count, translatedLines.Count);
                await FillMissingBatchLineByLineAsync(completed, batch, cancellationToken);
                continue;
            }

            for (var i = 0; i < batch.Count; i++)
            {
                completed[batch[i].index].Chinese = translatedLines[i];
            }
        }

        var suspiciousIndexes = GetSuspiciousDuplicateTranslationIndexes(completed);
        if (suspiciousIndexes.Count > 0)
        {
            _logger.LogWarning("GPT 補翻譯後仍有短句重複譯文，改用逐行補翻譯，count={Count}", suspiciousIndexes.Count);
            var suspiciousBatch = suspiciousIndexes
                .OrderBy(index => index)
                .Select(index =>
                {
                    completed[index].Chinese = string.Empty;
                    return (segment: completed[index], index);
                })
                .ToList();
            await FillMissingBatchLineByLineAsync(completed, suspiciousBatch, cancellationToken);
        }

        return completed;
    }

    private static bool HasCompleteChineseTranslations(IReadOnlyList<LyricSegment> segments)
        => segments.Count > 0 && segments.All(segment => HasUsableChineseTranslation(segment.Chinese));

    private static bool HasUsableChineseTranslation(string? chinese)
        => !string.IsNullOrWhiteSpace(chinese)
            && !string.Equals(chinese.Trim(), "翻譯中...", StringComparison.Ordinal);

    private async Task FillMissingBatchLineByLineAsync(
        List<LyricSegment> completed,
        IReadOnlyList<(LyricSegment segment, int index)> batch,
        CancellationToken cancellationToken)
    {
        if (_openAiWhisperClient == null || !_enableRuntimeOpenAiTranslation)
        {
            return;
        }

        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var japanese = item.segment.Japanese?.Trim();
            if (string.IsNullOrWhiteSpace(japanese))
            {
                continue;
            }

            var knownShortPhrase = TryTranslateKnownShortPhrase(japanese);
            if (!string.IsNullOrWhiteSpace(knownShortPhrase))
            {
                completed[item.index].Chinese = knownShortPhrase;
                continue;
            }

            try
            {
                var translated = await _openAiWhisperClient.TranslateToChineseAsync(japanese);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    completed[item.index].Chinese = translated.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPT 逐行補翻譯失敗，index={Index}", item.index);
            }
        }
    }

    private static string? TryTranslateKnownShortPhrase(string japanese)
    {
        var normalized = Regex.Replace(japanese.Trim(), @"\s+", string.Empty);
        var countPhraseMatch = Regex.Match(normalized, @"^(?<noun>言葉|痛み)の数だけ$");
        if (!countPhraseMatch.Success)
        {
            return null;
        }

        return countPhraseMatch.Groups["noun"].Value switch
        {
            "言葉" => "話語有多少",
            "痛み" => "痛苦有多少",
            _ => null
        };
    }

    private static List<LyricSegment> ClearSuspiciousDuplicateTranslations(IReadOnlyList<LyricSegment> segments)
    {
        var completed = segments.Select(segment => new LyricSegment
        {
            LyricID = segment.LyricID,
            TimeStamp = segment.TimeStamp,
            Japanese = segment.Japanese,
            Chinese = segment.Chinese,
            JapaneseRuby = segment.JapaneseRuby,
            Roman = segment.Roman
        }).ToList();

        var suspiciousIndexes = GetSuspiciousDuplicateTranslationIndexes(completed);

        foreach (var index in suspiciousIndexes)
        {
            completed[index].Chinese = string.Empty;
        }

        return completed;
    }

    private static HashSet<int> GetSuspiciousDuplicateTranslationIndexes(IReadOnlyList<LyricSegment> segments)
    {
        static string NormalizeJapanese(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return Regex.Replace(text, @"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]", "");
        }

        return segments
            .Select((segment, index) => (segment, index, JapaneseNorm: NormalizeJapanese(segment.Japanese ?? string.Empty), ChineseNorm: (segment.Chinese ?? string.Empty).Trim()))
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.JapaneseNorm)
                && item.JapaneseNorm.Length <= 12
                && !string.IsNullOrWhiteSpace(item.ChineseNorm)
                && item.ChineseNorm != "翻譯中...")
            .GroupBy(item => item.ChineseNorm)
            .Where(group => group.Select(item => item.JapaneseNorm).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(item => item.index))
            .ToHashSet();
    }

    private async Task<List<LyricSegment>?> TryResolveGptAsync(IReadOnlyList<LyricSegment> stableSegmentsToInsert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_enableRuntimeOpenAiTranslation)
        {
            _logger.LogInformation("runtime OpenAI 翻譯 fallback 已關閉，保留待後台補翻");
            return null;
        }

        if (_openAiWhisperClient == null || stableSegmentsToInsert.Count == 0)
        {
            return null;
        }

        try
        {
            var japaneseLines = stableSegmentsToInsert
                .Select(seg => (seg.Japanese ?? string.Empty).Trim())
                .ToList();

            if (japaneseLines.All(string.IsNullOrWhiteSpace))
            {
                return null;
            }

            var translatedLines = new List<string>();
            const int batchSize = 20;

            for (var offset = 0; offset < japaneseLines.Count; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = japaneseLines.Skip(offset).Take(batchSize).ToList();
                var combinedJapanese = string.Join("@", batch);
                var translated = await _openAiWhisperClient.BatchTranslateToChineseAsync(combinedJapanese);
                if (string.IsNullOrWhiteSpace(translated))
                {
                    _logger.LogWarning("GPT 翻譯批次回傳空白，offset={Offset}, count={Count}", offset, batch.Count);
                    return null;
                }

                var batchTranslatedLines = translated
                    .Split('@')
                    .Select(line => line.Trim())
                    .ToList();

                if (batchTranslatedLines.Count != batch.Count || batchTranslatedLines.Any(string.IsNullOrWhiteSpace))
                {
                    _logger.LogWarning("GPT 翻譯批次行數不符或含空白，offset={Offset}, expected={Expected}, actual={Actual}", offset, batch.Count, batchTranslatedLines.Count);
                    return null;
                }

                translatedLines.AddRange(batchTranslatedLines);
            }

            if (translatedLines.Count != stableSegmentsToInsert.Count)
            {
                _logger.LogWarning("GPT 翻譯總行數不符，expected={Expected}, actual={Actual}", stableSegmentsToInsert.Count, translatedLines.Count);
                return null;
            }

            _logger.LogInformation("翻譯來源：GPT 批次翻譯（{Count} 行）", translatedLines.Count);
            return stableSegmentsToInsert.Select((seg, i) => new LyricSegment
            {
                TimeStamp = seg.TimeStamp,
                Japanese = seg.Japanese,
                Chinese = translatedLines[i]
            }).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GPT 批次翻譯失敗");
            return null;
        }
    }
}
