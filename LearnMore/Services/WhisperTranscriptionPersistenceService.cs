using System.Text.Json;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperTranscriptionPersistenceService
{
    private readonly IOpenAiWhisperClientService _openAiClient;

    public WhisperTranscriptionPersistenceService(IOpenAiWhisperClientService openAiClient)
    {
        _openAiClient = openAiClient;
    }

    public async Task<IEnumerable<LyricSegment>> ParseTranscriptionToSegmentsAsync(string transcriptionJson)
    {
        var rawSegments = ParseRawSegments(transcriptionJson);
        var parsedSegments = new List<LyricSegment>();

        foreach (var rawSegment in rawSegments)
        {
            var (rubyText, chineseText) = await _openAiClient.ProcessJapaneseTextAsync(rawSegment.Text);
            parsedSegments.Add(new LyricSegment
            {
                TimeStamp = rawSegment.Start,
                Japanese = rawSegment.Text,
                Chinese = chineseText,
                JapaneseRuby = rubyText
            });
        }

        return parsedSegments;
    }

    public async Task<IEnumerable<LyricSegment>> ParseTranscriptionToSegmentsChineseAsync(string transcriptionJson)
    {
        var rawSegments = ParseRawSegments(transcriptionJson).ToList();
        if (rawSegments.Count == 0)
        {
            return Array.Empty<LyricSegment>();
        }

        var batchTranslations = await TryTranslateBatchAsync(rawSegments);
        if (batchTranslations is not null)
        {
            return rawSegments.Select((segment, index) => new LyricSegment
            {
                TimeStamp = segment.Start,
                Japanese = segment.Text,
                Chinese = batchTranslations[index]
            }).ToList();
        }

        var translatedSegments = new List<LyricSegment>();
        foreach (var rawSegment in rawSegments)
        {
            var chineseText = await _openAiClient.TranslateToChineseAsync(rawSegment.Text);
            translatedSegments.Add(new LyricSegment
            {
                TimeStamp = rawSegment.Start,
                Japanese = rawSegment.Text,
                Chinese = chineseText
            });
        }

        return translatedSegments;
    }

    private async Task<List<string>?> TryTranslateBatchAsync(List<RawWhisperSegment> rawSegments)
    {
        var combinedJapanese = string.Join("@", rawSegments.Select(segment => segment.Text));
        var batchTranslation = await _openAiClient.BatchTranslateToChineseAsync(combinedJapanese);
        if (string.IsNullOrWhiteSpace(batchTranslation))
        {
            return null;
        }

        var translatedLines = batchTranslation
            .Split('@', StringSplitOptions.TrimEntries)
            .Select(line => line.Trim())
            .ToList();

        return translatedLines.Count == rawSegments.Count ? translatedLines : null;
    }

    private static IEnumerable<RawWhisperSegment> ParseRawSegments(string transcriptionJson)
    {
        using var jsonDoc = JsonDocument.Parse(transcriptionJson);
        if (!jsonDoc.RootElement.TryGetProperty("segments", out var segmentArray))
        {
            throw new Exception("The 'segments' key was not found in the transcription JSON.");
        }

        var rawSegments = new List<RawWhisperSegment>();
        foreach (var segment in segmentArray.EnumerateArray())
        {
            if (!segment.TryGetProperty("start", out var startProp) ||
                !segment.TryGetProperty("text", out var textProp))
            {
                continue;
            }

            rawSegments.Add(new RawWhisperSegment(
                startProp.GetDouble(),
                textProp.GetString() ?? string.Empty));
        }

        return rawSegments;
    }

    private sealed record RawWhisperSegment(double Start, string Text);
}
