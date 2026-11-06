using LearnMore.Models;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public class AutomaticSongCreationValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingUploaderIsRejectedByEveryCreationMethod(string? email)
    {
        var service = new WhisperSongPersistenceService(new ConfigurationBuilder().Build(), null!, null!, null!,
            NullLogger<WhisperSongPersistenceService>.Instance);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.AddSongToDatabaseAsync(new(), email!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateSummonedSongAsync(new(), Array.Empty<LyricEntry>(), email!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateSongWithPlaceholdersAsync(new(), Array.Empty<LyricSegment>(), email!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingOriginalLyricsAreRejectedBeforeOpeningDatabase(string? original)
    {
        var service = new WhisperSongPersistenceService(new ConfigurationBuilder().Build(), null!, null!, null!,
            NullLogger<WhisperSongPersistenceService>.Instance);
        var rows = new List<LyricSegment> { new() { Japanese = "有効な歌詞" }, new() { Japanese = original! } };
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSongWithPlaceholdersAsync(new TranscribeRequest(), rows, "owner@example.invalid"));
        Assert.Equal("segments", error.ParamName);
    }

    [Fact]
    public async Task EmptyRecognitionIsRejectedBeforeOpeningDatabase()
    {
        var service = new WhisperSongPersistenceService(new ConfigurationBuilder().Build(), null!, null!, null!,
            NullLogger<WhisperSongPersistenceService>.Instance);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateSongWithPlaceholdersAsync(new TranscribeRequest(), Array.Empty<LyricSegment>(), "owner@example.invalid"));
        Assert.Equal("segments", error.ParamName);
    }
}
