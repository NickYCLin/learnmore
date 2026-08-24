using LearnMore.Controllers;
using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class WhisperPostProcessServiceTests
{
    [Fact]
    public void BuildEnrichmentRequests_ShouldUseRomajiModeForRomanColumn()
    {
        var (rubyRequest, romanRequest) = WhisperPostProcessService.BuildEnrichmentRequests("song-123");

        Assert.Equal("song-123", rubyRequest.SongUid);
        Assert.Equal("JapaneseRuby", rubyRequest.Column);
        Assert.Equal("furigana", rubyRequest.Mode);
        Assert.Equal("hiragana", rubyRequest.To);

        Assert.Equal("song-123", romanRequest.SongUid);
        Assert.Equal("Roman", romanRequest.Column);
        Assert.Equal("spaced", romanRequest.Mode);
        Assert.Equal("romaji", romanRequest.To);
    }
}
