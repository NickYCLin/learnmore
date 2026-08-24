using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public class OpenAiWhisperClientService : IOpenAiWhisperClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WhisperRuntimeOptions _options;
    private readonly ILogger<OpenAiWhisperClientService> _logger;

    public OpenAiWhisperClientService(
        IHttpClientFactory httpClientFactory,
        IOptions<WhisperRuntimeOptions> options,
        ILogger<OpenAiWhisperClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> TranscribeAudioAsync(string audioFilePath, string language)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
            throw new Exception("OpenAI API key is not configured.");

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var form = new MultipartFormDataContent();
        using var audioFileStream = File.OpenRead(audioFilePath);
        var audioContent = new StreamContent(audioFileStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(audioContent, "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent("whisper-1"), "model");
        if (!string.IsNullOrEmpty(language))
            form.Add(new StringContent(language), "language");
        form.Add(new StringContent("verbose_json"), "response_format");

        var response = await httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", form);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"OpenAI API error: {error}");
        }

        var jsonResponse = await response.Content.ReadAsStringAsync();
        SaveJsonToFile(jsonResponse, Path.GetFileNameWithoutExtension(audioFilePath));
        return jsonResponse;
    }

    public async Task<string?> BatchTranslateToChineseAsync(string combinedJapanese)
    {
        if (string.IsNullOrWhiteSpace(combinedJapanese))
            return null;

        string apiUrl = "https://api.openai.com/v1/chat/completions";
        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "你是專業的日文翻譯助手。使用者會傳給你多行日文，每行以 @ 符號分隔。請將每行翻譯成繁體中文，同樣用 @ 分隔回傳，行數必須與輸入完全一致，不要新增或刪減任何行。只回傳翻譯結果，不要附加任何說明。"
                },
                new { role = "user", content = combinedJapanese }
            }
        };

        try
        {
            var response = await httpClient.PostAsync(apiUrl,
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "BatchTranslateToChineseAsync failed with status {StatusCode}: {Error}",
                    (int)response.StatusCode,
                    TruncateForLog(error));
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseBody);
            return responseJson.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BatchTranslateToChineseAsync failed");
            return null;
        }
    }

    public async Task<string> TranslateToChineseAsync(string japaneseText)
    {
        string apiUrl = "https://api.openai.com/v1/chat/completions";
        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("OpenAI API key is not configured.");

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = "你是專業的翻譯助手，請將日文翻譯成繁體中文。" },
                new { role = "user", content = japaneseText }
            }
        };

        var response = await httpClient.PostAsync(apiUrl,
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning(
                "TranslateToChineseAsync failed with status {StatusCode}: {Error}",
                (int)response.StatusCode,
                TruncateForLog(error));
            throw new Exception($"Error from ChatGPT API: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var responseJson = JsonDocument.Parse(responseBody);
        return responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    public async Task<string?> TranslateSongTitleToTraditionalChineseAsync(string songTitle, string? artist = null)
    {
        if (string.IsNullOrWhiteSpace(songTitle))
            return null;

        string apiUrl = "https://api.openai.com/v1/chat/completions";
        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "你是日文歌曲資料整理助手。請把使用者提供的歌曲名稱翻成台灣繁體中文搜尋別名。只回傳一個最自然、最短、適合搜尋的中文歌名；不要加引號、括號、歌手名、說明、標點或多個候選。若原題已是中文或繁中可直接搜尋，回傳原題。"
                },
                new { role = "user", content = string.IsNullOrWhiteSpace(artist) ? songTitle : $"歌手：{artist}\n歌名：{songTitle}" }
            }
        };

        try
        {
            var response = await httpClient.PostAsync(apiUrl,
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
                return null;

            var responseBody = await response.Content.ReadAsStringAsync();
            var responseJson = JsonDocument.Parse(responseBody);
            var translated = responseJson.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return NormalizeSongTitleAlias(translated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TranslateSongTitleToTraditionalChineseAsync failed for {SongTitle}", songTitle);
            return null;
        }
    }

    public async Task<(string RubyText, string ChineseText)> ProcessJapaneseTextAsync(string text)
    {
        string apiUrl = "https://api.openai.com/v1/chat/completions";
        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new Exception("OpenAI API key is not configured.");

        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new[]
            {
                new { role = "system", content = "你是專業的日文語言處理助手，請將以下日文文本進行以下兩步處理：1. 轉換為包含 <ruby> 標籤的格式；2. 翻譯成繁體中文。結果請以 JSON 格式返回，格式如下：{\"ruby\": \"...\", \"chinese\": \"...\"}。" },
                new { role = "user", content = text }
            }
        };

        var response = await httpClient.PostAsync(apiUrl,
            new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Error from ChatGPT API: {response.StatusCode}");

        var responseBody = await response.Content.ReadAsStringAsync();
        var responseJson = JsonDocument.Parse(responseBody);
        var resultJson = responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(resultJson))
            return (string.Empty, string.Empty);

        using var result = JsonDocument.Parse(resultJson);
        string rubyText = result.RootElement.GetProperty("ruby").GetString() ?? string.Empty;
        string chineseText = result.RootElement.GetProperty("chinese").GetString() ?? string.Empty;
        return (rubyText, chineseText);
    }

    private string GetApiKey()
        => !string.IsNullOrWhiteSpace(_options.MyApiKey) ? _options.MyApiKey : _options.ApiKey;

    private static string TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Length <= 500 ? value : value[..500];
    }

    private static string? NormalizeSongTitleAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return null;

        var normalized = alias.Trim()
            .Trim('\ufeff')
            .Trim('"', '\'', '「', '」', '『', '』', '“', '”', '‘', '’')
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        if (normalized.Contains('：'))
        {
            var parts = normalized.Split('：', 2, StringSplitOptions.TrimEntries);
            normalized = parts.LastOrDefault(part => !string.IsNullOrWhiteSpace(part)) ?? normalized;
        }

        var separators = new[] { " / ", "／", "、", ",", "，", ";", "；" };
        foreach (var separator in separators)
        {
            var index = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                normalized = normalized[..index].Trim();
            }
        }

        normalized = normalized.Trim('"', '\'', '「', '」', '『', '』', '“', '”', '‘', '’', '.', '。', '！', '!');
        return normalized.Length is > 0 and <= 100 ? normalized : null;
    }

    private void SaveJsonToFile(string json, string fileName)
    {
        var outputPath = _options.WhisperJsonPath;
        if (string.IsNullOrEmpty(outputPath))
            throw new Exception("Whisper JSON path is not configured.");

        var directory = Path.Combine(outputPath, "WhisperJson");
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var filePath = Path.Combine(directory, $"{fileName}.json");
        File.WriteAllText(filePath, json);
    }
}
