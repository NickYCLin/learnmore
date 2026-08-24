using LearnMore.Models;
using Microsoft.Playwright;
using System.Net;
using System.Text.RegularExpressions;

namespace LearnMore.Services
{
    public class MarumaruCrawlerService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<MarumaruCrawlerService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public MarumaruCrawlerService(IConfiguration configuration, ILogger<MarumaruCrawlerService> logger, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// IIS / headless 環境相容的 browser launch options。
        /// 加入 --no-sandbox、--disable-dev-shm-usage 等 Chrome flag，
        /// 避免 Windows IIS worker process 沒有桌面環境而 crash。
        /// </summary>
        private BrowserTypeLaunchOptions BuildLaunchOptions()
        {
            var chromiumPath = _configuration["Playwright:ChromiumPath"];
            var options = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--no-first-run",
                    "--no-zygote",
                    "--single-process"
                },
                Timeout = 15000
            };

            if (!string.IsNullOrEmpty(chromiumPath))
            {
                options.ExecutablePath = chromiumPath;
            }

            return options;
        }

        /// <summary>
        /// 搜尋 marumaru 並抓取歌詞。
        /// Step 1: 用 Yahoo Japan HttpClient 搜尋 marumaru URL（不需要 Playwright）
        /// Step 2: 用 HttpClient 抓歌詞頁（SSR 頁面，不需要 JS 渲染）
        /// Fallback: 若 Yahoo 搜不到，用 Playwright + Google CSE 搜尋
        /// </summary>
        public async Task<List<(string Japanese, string Chinese)>?> SearchAndFetchAsync(string title, string artist)
        {
            // --- Step 1A: Yahoo Japan HttpClient 搜尋（快速，不需要 Playwright）---
            string? marumaruUrl = null;
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.Timeout = TimeSpan.FromSeconds(10);

                var query = $"site:marumaru-x.com {title} {artist} 歌詞";
                var searchUrl = $"https://search.yahoo.co.jp/search?p={Uri.EscapeDataString(query)}";
                var searchHtml = await client.GetStringAsync(searchUrl);

                var linkMatch = Regex.Match(searchHtml, @"marumaru-x\.com/japanese-song/play-[a-zA-Z0-9]+");
                if (linkMatch.Success)
                {
                    marumaruUrl = $"https://www.{linkMatch.Value}";
                    _logger.LogInformation("marumaru: Yahoo 搜尋找到 {Url}", marumaruUrl);
                }
                else
                {
                    _logger.LogInformation("marumaru: Yahoo 搜尋無結果 for {Title} {Artist}", title, artist);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "marumaru: Yahoo 搜尋失敗 for {Title} {Artist}", title, artist);
            }

            // --- Step 1B: Playwright + Google CSE 搜尋（fallback）---
            if (string.IsNullOrEmpty(marumaruUrl))
            {
                try
                {
                    using var playwright = await Playwright.CreateAsync();
                    var launchOptions = BuildLaunchOptions();

                    await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
                    await using var context = await browser.NewContextAsync();
                    var searchPage = await context.NewPageAsync();

                    try
                    {
                        var encodedTitle = Uri.EscapeDataString(title);
                        var encodedArtist = Uri.EscapeDataString(artist);
                        var cseUrl =
                            $"https://cse.google.com/cse?cx=006433377945535362806%3A1gjgvl5smaa&q={encodedTitle}+{encodedArtist}+%E6%AD%8C%E8%A9%9E";

                        await searchPage.GotoAsync(cseUrl, new PageGotoOptions { Timeout = 10000, WaitUntil = WaitUntilState.DOMContentLoaded });
                        await searchPage.WaitForTimeoutAsync(3000);

                        var anchor = await searchPage.QuerySelectorAsync("a[href*='marumaru-x.com/japanese-song/']");
                        if (anchor != null)
                        {
                            marumaruUrl = await anchor.GetAttributeAsync("href");
                            _logger.LogInformation("marumaru: Google CSE fallback 找到 {Url}", marumaruUrl);
                        }
                    }
                    finally
                    {
                        await searchPage.CloseAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "marumaru: Google CSE fallback 也失敗 for {Title} {Artist}", title, artist);
                }
            }

            if (string.IsNullOrEmpty(marumaruUrl))
                return null;

            // --- Step 2: 用 HttpClient 抓歌詞頁 ---
            return await FetchLyricsWithHttpClient(marumaruUrl);
        }

        /// <summary>
        /// 用 HttpClient 直接抓 marumaru 歌詞頁（SSR 頁面，不需要 JS 渲染）。
        /// 日文從 select#input-repeat-start 的 option 取得，中文從 p.lyrics-translate.translate-zh 取得。
        /// </summary>
        private async Task<List<(string Japanese, string Chinese)>?> FetchLyricsWithHttpClient(string marumaruUrl)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                client.Timeout = TimeSpan.FromSeconds(15);

                var html = await client.GetStringAsync(marumaruUrl);

                var result = ExtractLyricsFromHtml(html);
                if (result == null || result.Count == 0)
                {
                    _logger.LogInformation("marumaru HttpClient: 歌詞解析結果為空 at {Url}", marumaruUrl);
                    return null;
                }

                var cnCount = result.Count(line => !string.IsNullOrWhiteSpace(line.Chinese));
                _logger.LogInformation("marumaru HttpClient: 成功取得 {JpCount} 行日文, {CnCount} 行中文 from {Url}", result.Count, cnCount, marumaruUrl);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "marumaru FetchLyricsWithHttpClient failed for {Url}", marumaruUrl);
                return null;
            }
        }

        public static List<(string Japanese, string Chinese)>? ExtractLyricsFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            static string CleanText(string value)
            {
                var withoutTags = Regex.Replace(value, @"<[^>]+>", string.Empty);
                var decoded = WebUtility.HtmlDecode(withoutTags);
                return Regex.Replace(decoded, @"\s+", " ").Trim();
            }

            // 日文歌詞：取 select#input-repeat-start 裡的 option。
            var startSelectMatch = Regex.Match(html, @"id=""input-repeat-start""[^>]*>(.*?)</select>", RegexOptions.Singleline);
            if (!startSelectMatch.Success)
            {
                return null;
            }

            var optionMatches = Regex.Matches(startSelectMatch.Groups[1].Value, @"<option\b[^>]*value=""\d+""[^>]*>\(\d+\)(.*?)</option>", RegexOptions.Singleline);
            var jpLines = optionMatches.Cast<Match>()
                .Select(m => CleanText(m.Groups[1].Value))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (jpLines.Count == 0)
            {
                return null;
            }

            // 中文翻譯：marumaru 現行 HTML 是 class="lyrics-translate-zh ... translate-zh"。
            // 舊 selector 只接受 class 以 lyrics-translate 開頭且之後出現 translate-zh，
            // 一旦站方調整 class 順序或使用 lyrics-translate-zh 就會抓不到中文。
            var cnMatches = Regex.Matches(
                html,
                @"<p\b(?=[^>]*\bclass=""[^""]*lyrics-translate[^""]*"")(?=[^>]*\bclass=""[^""]*translate-zh[^""]*"")[^>]*>(.*?)</p>",
                RegexOptions.Singleline);
            var cnLines = cnMatches.Cast<Match>()
                .Select(m => CleanText(m.Groups[1].Value))
                .ToList();

            var result = new List<(string Japanese, string Chinese)>();
            for (int i = 0; i < jpLines.Count; i++)
            {
                if (LyricLineFilter.ShouldSkipSyncedLyricLine(jpLines[i]))
                {
                    continue;
                }

                var cn = i < cnLines.Count ? cnLines[i] : string.Empty;
                result.Add((jpLines[i], cn));
            }

            return result;
        }

        /// <summary>
        /// 搜尋 marumaru，回傳歌詞頁 URL。
        /// @deprecated 請改用 SearchAndFetchAsync；保留此方法供相容性使用。
        /// </summary>
        public async Task<string?> SearchSongUrlAsync(string title, string artist)
        {
            using var playwright = await Playwright.CreateAsync();
            var launchOptions = BuildLaunchOptions();

            await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                var encodedTitle = Uri.EscapeDataString(title);
                var encodedArtist = Uri.EscapeDataString(artist);
                var searchUrl =
                    $"https://cse.google.com/cse?cx=006433377945535362806%3A1gjgvl5smaa&q={encodedTitle}+{encodedArtist}+%E6%AD%8C%E8%A9%9E";

                await page.GotoAsync(searchUrl, new PageGotoOptions { Timeout = 10000, WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.WaitForTimeoutAsync(3000);

                var anchor = await page.QuerySelectorAsync("a[href*='marumaru-x.com/japanese-song/']");
                if (anchor == null)
                {
                    _logger.LogInformation("marumaru: no result found for {Title} {Artist}", title, artist);
                    return null;
                }

                var href = await anchor.GetAttributeAsync("href");
                return href;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "marumaru SearchSongUrlAsync failed");
                return null;
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        /// <summary>
        /// 抓歌詞頁的日文和中文歌詞。
        /// @deprecated 請改用 SearchAndFetchAsync；保留此方法供相容性使用。
        /// </summary>
        public async Task<List<(string Japanese, string Chinese)>?> FetchLyricsAsync(string marumaruUrl)
        {
            using var playwright = await Playwright.CreateAsync();
            var launchOptions = BuildLaunchOptions();

            await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            try
            {
                await page.GotoAsync(marumaruUrl, new PageGotoOptions { Timeout = 15000, WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.WaitForTimeoutAsync(4000);

                var japaneseElements = await page.QuerySelectorAllAsync(".lyrics-source");
                var chineseElements = await page.QuerySelectorAllAsync(".lyrics-translate.translate-zh");

                if (japaneseElements.Count == 0)
                {
                    _logger.LogInformation("marumaru: no .lyrics-source found at {Url}", marumaruUrl);
                    return null;
                }

                var result = new List<(string Japanese, string Chinese)>();
                var count = Math.Min(japaneseElements.Count, chineseElements.Count);

                for (int i = 0; i < count; i++)
                {
                    var japanese = (await japaneseElements[i].InnerTextAsync()).Trim();
                    var chinese = (await chineseElements[i].InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(japanese))
                    {
                        result.Add((japanese, chinese));
                    }
                }

                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "marumaru FetchLyricsAsync failed for {Url}", marumaruUrl);
                return null;
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        /// <summary>
        /// 對齊歌詞和時間戳。將 marumaru 的日文（含 ruby）和中文翻譯，
        /// 對應到 yt-dlp 時間戳段落，回傳完整的 LyricSegment 清單。
        /// </summary>
        /// <summary>
        /// 用 LRC 精準時間戳 + marumaru 日文/中文對齊。
        /// LRC 提供時間戳，marumaru 提供正確日文和中文翻譯。
        /// 使用全域最佳匹配：對每個 LRC 行找整個 marumaru 中最相似的一行。
        /// </summary>
        public List<LyricSegment> AlignWithLrc(
            List<(string Japanese, string Chinese)> marumaruLyrics,
            List<(double TimeStamp, string Japanese)> lrcLines)
        {
            static string Normalize(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                // 只保留日文字元（漢字 + 假名）做比對
                return Regex.Replace(text, @"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]", "");
            }

            // 計算相似度（0~1，1=完全相同）
            static double Similarity(string a, string b)
            {
                if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
                int maxLen = Math.Max(a.Length, b.Length);
                if (maxLen == 0) return 1.0;
                int dist = LevenshteinDistanceStatic(a, b);
                return 1.0 - (double)dist / maxLen;
            }

            // 靜態版本的 Levenshtein
            static int LevenshteinDistanceStatic(string s, string t)
            {
                if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
                if (string.IsNullOrEmpty(t)) return s.Length;
                int n = s.Length, m = t.Length;
                var d = new int[n + 1, m + 1];
                for (int i = 0; i <= n; i++) d[i, 0] = i;
                for (int j = 0; j <= m; j++) d[0, j] = j;
                for (int i = 1; i <= n; i++)
                {
                    for (int j = 1; j <= m; j++)
                    {
                        int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                        d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    }
                }
                return d[n, m];
            }

            var result = new List<LyricSegment>();
            var normalizedMarumaru = marumaruLyrics
                .Select(m => (Lyrics: m, Norm: Normalize(m.Japanese)))
                .ToList();

            // 記錄每個 marumaru 行已被使用的次數
            var usedCount = new int[normalizedMarumaru.Count];

            // 先計算 marumaru 中每個文字出現幾次（用於判斷是否為重複歌詞）
            var marumaruTextCount = normalizedMarumaru
                .GroupBy(m => m.Norm)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (ts, lrcJp) in lrcLines)
            {
                var lrcNorm = Normalize(lrcJp);
                if (string.IsNullOrEmpty(lrcNorm))
                {
                    // 空行保留原文，不翻譯
                    result.Add(new LyricSegment { TimeStamp = ts, Japanese = lrcJp, Chinese = "" });
                    continue;
                }

                // 找整個 marumaru 中最相似的一行
                int bestIdx = -1;
                double bestSim = 0.0;

                for (int i = 0; i < normalizedMarumaru.Count; i++)
                {
                    var maruNorm = normalizedMarumaru[i].Norm;
                    if (string.IsNullOrEmpty(maruNorm)) continue;

                    // 計算相似度
                    double sim = Similarity(lrcNorm, maruNorm);

                    // 也檢查包含關係（給予高分）
                    bool isContained = maruNorm.Contains(lrcNorm);  // LRC 是 marumaru 的子句
                    if (lrcNorm.Contains(maruNorm) || isContained)
                    {
                        // 包含關係：用較短的長度比例作為相似度
                        int shorter = Math.Min(lrcNorm.Length, maruNorm.Length);
                        int longer = Math.Max(lrcNorm.Length, maruNorm.Length);
                        double containSim = (double)shorter / longer;
                        sim = Math.Max(sim, containSim + 0.3); // 包含關係加分
                    }

                    // 如果這行已被用過，且 marumaru 中這個文字只出現一次，降低優先度
                    // 但如果是「包含關係」（LRC 是 marumaru 的子句），則允許重複使用
                    int textOccurrence = marumaruTextCount.GetValueOrDefault(maruNorm, 1);
                    if (usedCount[i] >= textOccurrence && !isContained)
                    {
                        sim *= 0.5; // 已用完的行降低分數（但子句不降分）
                    }

                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        bestIdx = i;
                    }
                }

                var bestMatchedNorm = bestIdx >= 0 ? normalizedMarumaru[bestIdx].Norm : string.Empty;
                var hasExactOrContainMatch = bestIdx >= 0
                    && (bestMatchedNorm == lrcNorm
                        || bestMatchedNorm.Contains(lrcNorm)
                        || lrcNorm.Contains(bestMatchedNorm));

                // 短句只差一兩個字時，Levenshtein 分數仍可能偏高，例如「痛みの数だけ」
                // 會誤配到「言葉の数だけ」。短句非包含/完全匹配時採用較高門檻，避免錯翻。
                var minimumSimilarity = lrcNorm.Length <= 8 || bestMatchedNorm.Length <= 8 ? 0.85 : 0.65;

                if (bestIdx >= 0 && (hasExactOrContainMatch || bestSim >= minimumSimilarity))
                {
                    var matched = normalizedMarumaru[bestIdx].Lyrics;
                    var matchedNorm = bestMatchedNorm;
                    var chinese = matched.Chinese ?? "";

                    // 智慧拆分翻譯：如果 LRC 只是 marumaru 的一部分，嘗試拆分翻譯
                    if (!string.IsNullOrEmpty(chinese) && matchedNorm.Contains(lrcNorm) && matchedNorm.Length > lrcNorm.Length)
                    {
                        // LRC 是 marumaru 的子字串，計算在 marumaru 中的位置比例
                        int startPos = matchedNorm.IndexOf(lrcNorm);
                        double startRatio = (double)startPos / matchedNorm.Length;
                        double endRatio = (double)(startPos + lrcNorm.Length) / matchedNorm.Length;

                        // 按比例拆分翻譯。中文常沒有空格，不能只依賴空白分詞，
                        // 否則同一整句中文會被重複貼到多個 LRC 短句。
                        var chineseParts = chinese.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
                        if (chineseParts.Length >= 2)
                        {
                            int totalChars = chinese.Replace(" ", "").Replace("　", "").Length;
                            int startCharIdx = (int)Math.Floor(startRatio * totalChars);
                            int endCharIdx = (int)Math.Ceiling(endRatio * totalChars);

                            // 找出對應的翻譯片段
                            var selectedParts = new List<string>();
                            int charCount = 0;
                            foreach (var part in chineseParts)
                            {
                                int partEnd = charCount + part.Length;
                                // 如果這個片段和目標範圍有重疊
                                if (partEnd > startCharIdx && charCount < endCharIdx)
                                {
                                    selectedParts.Add(part);
                                }
                                charCount = partEnd;
                            }

                            if (selectedParts.Count > 0)
                            {
                                chinese = string.Join(" ", selectedParts);
                            }
                        }
                        else
                        {
                            var compactChinese = chinese.Replace(" ", "").Replace("　", "");
                            if (compactChinese.Length >= 2)
                            {
                                int startCharIdx = Math.Clamp((int)Math.Round(startRatio * compactChinese.Length), 0, compactChinese.Length - 1);
                                int endCharIdx = Math.Clamp((int)Math.Round(endRatio * compactChinese.Length), startCharIdx + 1, compactChinese.Length);
                                chinese = compactChinese.Substring(startCharIdx, endCharIdx - startCharIdx);
                            }
                        }
                    }

                    result.Add(new LyricSegment
                    {
                        TimeStamp = ts,
                        Japanese = lrcJp,             // 保留 LRC 原始日文（時間戳對應正確）
                        Chinese = chinese,            // marumaru 中文翻譯（可能已拆分）
                    });
                    usedCount[bestIdx]++;
                }
                else
                {
                    // 找不到匹配：保留 LRC 原文，翻譯留空
                    result.Add(new LyricSegment
                    {
                        TimeStamp = ts,
                        Japanese = lrcJp,
                        Chinese = ""  // 無翻譯，不繼承（避免錯誤的翻譯重複出現）
                    });
                }
            }

            return result;
        }

        public List<LyricSegment> AlignLyricsWithTimestamps(
            List<(string Japanese, string Chinese)> marumaruLyrics,
            List<LyricSegment> timestampedSegments)
        {
            // ════════════════════════════════════════════════════════════
            // 以 marumaru 為主：日文歌詞和中文翻譯都用 marumaru 的。
            // YouTube 只提供時間戳。
            //
            // 策略：
            //   1. 把 YouTube 的時間戳均分給 marumaru 的行數
            //   2. 用模糊比對微調時間戳（如果 YouTube 的某行跟 marumaru 對得上）
            // ════════════════════════════════════════════════════════════

            static string Normalize(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                return Regex.Replace(text, @"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]", "");
            }

            var result = new List<LyricSegment>();

            if (marumaruLyrics.Count == 0)
                return timestampedSegments;

            double GetFallbackTimestamp(int marumaruIndex)
            {
                if (timestampedSegments.Count == 0)
                    return marumaruIndex * 5.0;

                if (timestampedSegments.Count == 1 || marumaruLyrics.Count == 1)
                    return timestampedSegments[0].TimeStamp;

                var proportionalIndex = (int)Math.Floor(
                    (double)marumaruIndex * (timestampedSegments.Count - 1) / Math.Max(1, marumaruLyrics.Count - 1));
                proportionalIndex = Math.Clamp(proportionalIndex, 0, timestampedSegments.Count - 1);
                return timestampedSegments[proportionalIndex].TimeStamp;
            }

            // 建立 YouTube 時間戳索引（用於模糊比對）
            var tsNormalized = timestampedSegments
                .Select(s => (Segment: s, Normalized: Normalize(s.Japanese)))
                .ToList();

            // 對每行 marumaru 歌詞，嘗試找最佳 YouTube 時間戳
            int tsPointer = 0;
            int windowSize = Math.Max(8, Math.Abs(marumaruLyrics.Count - timestampedSegments.Count) + 5);

            for (int m = 0; m < marumaruLyrics.Count; m++)
            {
                var maruJp = marumaruLyrics[m].Japanese;
                var maruCn = marumaruLyrics[m].Chinese;
                var maruNorm = Normalize(maruJp);

                double bestTimestamp;

                // 嘗試從 YouTube 時間戳找對應
                int windowEnd = Math.Min(tsPointer + windowSize, tsNormalized.Count);
                int bestIdx = -1;
                int bestScore = int.MaxValue;

                for (int i = tsPointer; i < windowEnd; i++)
                {
                    var score = LevenshteinDistance(maruNorm, tsNormalized[i].Normalized);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestIdx = i;
                    }
                }

                // 檢查是否匹配（寬鬆閾值）
                var threshold = maruNorm.Length <= 4
                    ? 1
                    : Math.Max(3, maruNorm.Length / 2);
                bool matched = bestIdx >= 0 && bestScore <= threshold;

                // 包含關係 fallback
                if (!matched && !string.IsNullOrEmpty(maruNorm))
                {
                    for (int i = tsPointer; i < windowEnd; i++)
                    {
                        var tsNorm = tsNormalized[i].Normalized;
                        if (string.IsNullOrEmpty(tsNorm)) continue;
                        if (tsNorm.Contains(maruNorm) || maruNorm.Contains(tsNorm))
                        {
                            bestIdx = i;
                            matched = true;
                            break;
                        }
                        // 半行包含
                        int halfLen = Math.Min(maruNorm.Length, tsNorm.Length) / 2;
                        if (halfLen >= 3)
                        {
                            if (tsNorm.Contains(maruNorm.Substring(0, halfLen)) ||
                                maruNorm.Contains(tsNorm.Substring(0, halfLen)))
                            {
                                bestIdx = i;
                                matched = true;
                                break;
                            }
                        }
                    }
                }

                if (matched)
                {
                    bestTimestamp = tsNormalized[bestIdx].Segment.TimeStamp;
                    if (bestIdx >= tsPointer)
                        tsPointer = bestIdx + 1;
                }
                else
                {
                    // 沒有可信文字匹配：保留來源時間錨的行序比例，避免短句被錯配到後段重複字幕。
                    bestTimestamp = GetFallbackTimestamp(m);
                }

                if (result.Count > 0 && bestTimestamp < result[^1].TimeStamp)
                {
                    var nextOrderedTimestamp = timestampedSegments
                        .Where(segment => segment.TimeStamp >= result[^1].TimeStamp)
                        .Select(segment => (double?)segment.TimeStamp)
                        .FirstOrDefault();
                    bestTimestamp = nextOrderedTimestamp ?? result[^1].TimeStamp;
                }

                result.Add(new LyricSegment
                {
                    TimeStamp = bestTimestamp,
                    Japanese = maruJp,
                    Chinese = maruCn,
                });
            }

            return result;
        }

        /// <summary>
        /// 從巴哈姆特論壇搜尋歌詞翻譯，回傳純中文翻譯行清單。
        /// 流程：
        ///   1. 用 Google site:gamer.com.tw 搜尋，找巴哈論壇帖子連結
        ///   2. 進帖子抓含有中文翻譯的段落（過濾掉純日文行）
        ///   3. 若翻譯行數與 segmentCount 差距過大（超過 50%），視為無效 → return null
        /// </summary>
        /// <param name="title">歌名</param>
        /// <param name="artist">歌手/樂團名</param>
        /// <param name="segmentCount">時間戳 segments 數量，用於驗證翻譯行數是否合理</param>
        public async Task<List<string>?> SearchBahaLyricsAsync(string title, string artist, int segmentCount = 0)
        {
            using var playwright = await Playwright.CreateAsync();
            var launchOptions = BuildLaunchOptions();

            await using var browser = await playwright.Chromium.LaunchAsync(launchOptions);
            await using var context = await browser.NewContextAsync();

            // --- Step 1: Google 搜尋巴哈姆特論壇帖子 ---
            string? bahaUrl = null;
            var searchPage = await context.NewPageAsync();
            try
            {
                var encodedQuery = Uri.EscapeDataString($"site:gamer.com.tw {title} {artist} 歌詞翻譯");
                var searchUrl = $"https://www.google.com/search?q={encodedQuery}&hl=zh-TW";

                await searchPage.GotoAsync(searchUrl, new PageGotoOptions
                {
                    Timeout = 12000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
                await searchPage.WaitForTimeoutAsync(2000);

                // 找第一個 gamer.com.tw 連結（優先抓論壇帖子 B.php 或 C.php）
                var anchors = await searchPage.QuerySelectorAllAsync("a[href*='gamer.com.tw']");
                foreach (var anchor in anchors)
                {
                    var href = await anchor.GetAttributeAsync("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    // 優先選擇論壇帖子（B.php = 看板文章, C.php = 留言）
                    if (href.Contains("forum.gamer.com.tw/B.php") ||
                        href.Contains("forum.gamer.com.tw/C.php") ||
                        href.Contains("gamer.com.tw") && href.Contains("歌詞"))
                    {
                        bahaUrl = href;
                        break;
                    }
                }

                // 若第一輪沒找到，再放寬：任何 gamer.com.tw/B 或 /C 連結都接受
                if (string.IsNullOrEmpty(bahaUrl))
                {
                    foreach (var anchor in anchors)
                    {
                        var href = await anchor.GetAttributeAsync("href");
                        if (!string.IsNullOrEmpty(href) &&
                            (href.Contains("forum.gamer.com.tw/B.php") ||
                             href.Contains("forum.gamer.com.tw/C.php")))
                        {
                            bahaUrl = href;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(bahaUrl))
                {
                    // 再試一次：直接搜巴哈姆特站內（不限制 artist）
                    var fallbackQuery = Uri.EscapeDataString($"site:forum.gamer.com.tw {title} 歌詞");
                    var fallbackUrl = $"https://www.google.com/search?q={fallbackQuery}&hl=zh-TW";
                    await searchPage.GotoAsync(fallbackUrl, new PageGotoOptions
                    {
                        Timeout = 10000,
                        WaitUntil = WaitUntilState.DOMContentLoaded
                    });
                    await searchPage.WaitForTimeoutAsync(2000);

                    var fallbackAnchors = await searchPage.QuerySelectorAllAsync("a[href*='forum.gamer.com.tw']");
                    foreach (var anchor in fallbackAnchors)
                    {
                        var href = await anchor.GetAttributeAsync("href");
                        if (!string.IsNullOrEmpty(href) &&
                            (href.Contains("/B.php") || href.Contains("/C.php")))
                        {
                            bahaUrl = href;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(bahaUrl))
                {
                    _logger.LogInformation("巴哈: no result found for {Title} {Artist}", title, artist);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "巴哈 SearchBahaLyricsAsync (search step) failed");
                return null;
            }
            finally
            {
                await searchPage.CloseAsync();
            }

            // --- Step 2: 進帖子頁面抓中文翻譯 ---
            var lyricsPage = await context.NewPageAsync();
            try
            {
                await lyricsPage.GotoAsync(bahaUrl, new PageGotoOptions
                {
                    Timeout = 15000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
                await lyricsPage.WaitForTimeoutAsync(3000);

                // 抓帖子主文（巴哈論壇的 .c-article__content 或 .pre-content）
                var contentSelectors = new[]
                {
                    ".c-article__content",
                    ".pre-content",
                    ".post-content",
                    "#ContentBody"
                };

                string? rawContent = null;
                foreach (var selector in contentSelectors)
                {
                    var el = await lyricsPage.QuerySelectorAsync(selector);
                    if (el != null)
                    {
                        rawContent = await el.InnerTextAsync();
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(rawContent))
                {
                    _logger.LogInformation("巴哈: no content found at {Url}", bahaUrl);
                    return null;
                }

                // --- Step 3: 從內文解析出中文翻譯行 ---
                var lines = rawContent
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                // 中文翻譯行：含有中文字元（\u4e00-\u9fff），且日文假名比例低
                var chineseLines = new List<string>();
                var japaneseCharRegex = new Regex(@"[\u3040-\u309F\u30A0-\u30FF]"); // 平/片假名
                var chineseCharRegex = new Regex(@"[\u4e00-\u9fff]");

                foreach (var line in lines)
                {
                    int chineseCount = chineseCharRegex.Matches(line).Count;
                    int japaneseCount = japaneseCharRegex.Matches(line).Count;

                    // 條件：有中文字、且日文假名少於中文字（避免抓到日文行）
                    // 同時過濾掉版面資訊（短於 2 字或超過 200 字的行）
                    if (chineseCount >= 2 &&
                        japaneseCount < chineseCount &&
                        line.Length >= 2 &&
                        line.Length <= 200)
                    {
                        chineseLines.Add(line);
                    }
                }

                if (chineseLines.Count == 0)
                {
                    _logger.LogInformation("巴哈: no Chinese translation lines found at {Url}", bahaUrl);
                    return null;
                }

                // --- Step 4: 驗證行數是否與 segments 接近 ---
                if (segmentCount > 0)
                {
                    var ratio = (double)chineseLines.Count / segmentCount;
                    // 允許行數在 50%～200% 之間（歌詞可能有重複段落）
                    if (ratio < 0.5 || ratio > 2.0)
                    {
                        _logger.LogInformation(
                            "巴哈: line count mismatch (got {Got}, expected ~{Expected}), skipping",
                            chineseLines.Count, segmentCount);
                        return null;
                    }
                }

                _logger.LogInformation("巴哈: found {Count} Chinese lines from {Url}", chineseLines.Count, bahaUrl);
                return chineseLines;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "巴哈 SearchBahaLyricsAsync (lyrics step) failed for {Url}", bahaUrl);
                return null;
            }
            finally
            {
                await lyricsPage.CloseAsync();
            }
        }

        // Levenshtein 距離（簡單版）
        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int[,] dp = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }
    }
}
