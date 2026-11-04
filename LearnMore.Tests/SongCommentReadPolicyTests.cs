using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public class SongCommentReadPolicyTests
{
    [Theory]
    [InlineData(11, false, true)]
    [InlineData(22, false, true)]
    [InlineData(33, true, true)]
    [InlineData(44, true, true)]
    [InlineData(55, false, false)]
    [InlineData(null, false, false)]
    [InlineData(6, false, false)]
    public void PrivateBodyAndInheritedRepliesRequireStableIdentity(int? viewerId, bool manager, bool expected)
    {
        Assert.Equal(expected, SongCommentReadPolicy.CanRead(true, "song-a", "song-a", viewerId, 11, 22, manager));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(22, false)]
    [InlineData(33, true)]
    [InlineData(null, false)]
    public void OtherSongNeverPassesEvenForManagerOrPublicComment(int? viewerId, bool manager)
    {
        Assert.False(SongCommentReadPolicy.CanRead(true, "song-a", "song-b", viewerId, 11, 22, manager));
        Assert.False(SongCommentReadPolicy.CanRead(false, "song-a", "song-b", viewerId, 11, 22, manager));
    }

    [Fact]
    public void MissingIdentityCannotGainPrivateAccessThroughNullEqualityOrRoleFlag()
    {
        Assert.False(SongCommentReadPolicy.CanRead(true, "song-a", "song-a", null, null, null, true));
        Assert.False(SongCommentReadPolicy.CanRead(true, "song-a", "song-a", 55, null, null, false));
        Assert.True(SongCommentReadPolicy.CanRead(false, "song-a", "song-a", null, null, null, false));
    }
}
