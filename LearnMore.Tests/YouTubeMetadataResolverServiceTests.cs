using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public class YouTubeMetadataResolverServiceTests
{
    [Fact]
    public async Task ResolveAsync_WithExistingTitleAndArtist_ReturnsInputWithoutExternalLookup()
    {
        var service = CreateService();

        var result = await service.ResolveAsync(
            "https://youtu.be/example",
            "Song Title",
            "Artist Name",
            CancellationToken.None);

        Assert.Equal("Song Title", result.Title);
        Assert.Equal("Artist Name", result.Artist);
    }

    [Fact]
    public async Task ResolveAsync_WithExistingTitleAndArtist_NormalizesPlaylistDecoration()
    {
        var service = CreateService();

        var result = await service.ResolveAsync(
            "https://youtu.be/example",
            "【第75回NHK紅白歌合戦 歌唱曲】踊り子",
            "Vaundy",
            CancellationToken.None);

        Assert.Equal("踊り子", result.Title);
        Assert.Equal("Vaundy", result.Artist);
    }

    [Fact]
    public async Task ResolveAsync_WithExistingTitleAndArtist_PreservesVersionParentheses()
    {
        var service = CreateService();

        var result = await service.ResolveAsync(
            "https://youtu.be/example",
            "Electricity (Arca Remix)",
            "宇多田ヒカル",
            CancellationToken.None);

        Assert.Equal("Electricity (Arca Remix)", result.Title);
        Assert.Equal("宇多田ヒカル", result.Artist);
    }

    [Fact]
    public async Task ResolveAsync_WithExistingTitleAndArtist_PreservesTrailingApostrophe()
    {
        var service = CreateService();

        var result = await service.ResolveAsync(
            "https://youtu.be/example",
            "KeepTryin'",
            "宇多田ヒカル",
            CancellationToken.None);

        Assert.Equal("KeepTryin'", result.Title);
        Assert.Equal("宇多田ヒカル", result.Artist);
    }

    [Fact]
    public async Task ResolveAsync_WithCancelledToken_ThrowsBeforeStartingYtDlp()
    {
        var service = CreateService();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ResolveAsync(
                "https://youtu.be/example",
                string.Empty,
                string.Empty,
                cancellationTokenSource.Token));
    }

    [Fact]
    public void TryParseDurationSeconds_WithNumericDuration_ReturnsSeconds()
    {
        Assert.Equal(361.42, YouTubeMetadataResolverService.TryParseDurationSeconds("361.42"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NA")]
    public void TryParseDurationSeconds_WithMissingOrNonNumericDuration_ReturnsNull(string? rawDuration)
    {
        Assert.Null(YouTubeMetadataResolverService.TryParseDurationSeconds(rawDuration));
    }

    [Theory]
    [InlineData("ヨルシカ - 晴る（OFFICIAL VIDEO）", "晴る", "ヨルシカ")]
    [InlineData("ヨルシカ - アポリア（OFFICIAL VIDEO）", "アポリア", "ヨルシカ / n-buna")]
    [InlineData("back number - どうしてもどうしても [Official Video]", "どうしてもどうしても", "back number")]
    [InlineData("【第75回NHK紅白歌合戦 歌唱曲】踊り子", "踊り子", "Vaundy")]
    public void TryNormalizeYouTubeMetadata_StripsOfficialVideoTagsAndArtistPrefix(string rawTitle, string expectedTitle, string rawArtist)
    {
        var result = YouTubeMetadataResolverService.TryNormalizeYouTubeMetadata(rawTitle, rawArtist);

        Assert.NotNull(result);
        Assert.Equal(expectedTitle, result.Value.Title);
        Assert.Equal(rawArtist, result.Value.Artist);
    }

    [Theory]
    [InlineData("ヨルシカ - チノカテ", "ヨルシカ / n-buna", "チノカテ")]
    [InlineData("back number - どうしてもどうしても", "back number", "どうしてもどうしても")]
    [InlineData("ハチ - 砂の惑星 feat.初音ミク", "ハチ (米津玄師)", "砂の惑星 feat.初音ミク")]
    public void StripArtistPrefixFromTitle_WithMatchingArtist_RemovesPrefix(string rawTitle, string rawArtist, string expected)
    {
        Assert.Equal(expected, YouTubeMetadataResolverService.StripArtistPrefixFromTitle(rawTitle, rawArtist));
    }

    private static YouTubeMetadataResolverService CreateService()
    {
        return new YouTubeMetadataResolverService(
            Microsoft.Extensions.Options.Options.Create(new WhisperRuntimeOptions()),
            null!,
            NullLogger<YouTubeMetadataResolverService>.Instance);
    }
}
