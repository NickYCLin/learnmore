using LearnMore.Services;
using Xunit;

namespace LearnMore.Tests;

public sealed class AudioStemPublicationGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationWaitsForSongCheckAndNeverWritesDeletedSong(bool exists)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "learnmore-publication-test-" + Guid.NewGuid());
        try
        {
            var pending = AudioStemPublicationGuard.PublishAsync(() => completion.Task, () =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "instrumental.flac"), "synthetic fixture");
                File.WriteAllText(Path.Combine(root, "vocals.flac"), "synthetic fixture");
            });
            Assert.False(pending.IsCompleted);
            Assert.False(Directory.Exists(root));
            completion.SetResult(exists);
            Assert.Equal(exists, await pending);
            Assert.Equal(exists, Directory.Exists(root));
            if (exists) Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FailedSongCheckNeverPublishes()
    {
        var published = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AudioStemPublicationGuard.PublishAsync(
            () => Task.FromException<bool>(new InvalidOperationException("database unavailable")), () => published = true));
        Assert.False(published);
    }

    [Fact]
    public async Task CancellationAfterCheckNeverPublishes()
    {
        using var cancellation = new CancellationTokenSource();
        var published = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => AudioStemPublicationGuard.PublishAsync(() =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        }, () => published = true, cancellation.Token));
        Assert.False(published);
    }

    [Fact]
    public async Task FileFailurePropagatesInsteadOfCompleting()
    {
        await Assert.ThrowsAsync<IOException>(() => AudioStemPublicationGuard.PublishAsync(
            () => Task.FromResult(true), () => throw new IOException("disk unavailable")));
    }

    [Fact]
    public void SqlLockAndBothProcessorsKeepPublicationInsideRegistration()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var service = File.ReadAllText(Path.Combine(root, "LearnMore/Services/AudioStemJobService.cs"));
        Assert.Contains("dbo.Songs WITH (UPDLOCK, HOLDLOCK) WHERE SongUid = @SongUid", service);
        Assert.Contains("existsCommand.ExecuteScalarAsync(cancellationToken)", service);
        Assert.Contains("connection, transaction);\n            existsCommand", service);
        var guard = service.IndexOf("AudioStemPublicationGuard.PublishAsync", StringComparison.Ordinal);
        Assert.True(service.IndexOf("DELETE FROM dbo.SongAudioStems", guard, StringComparison.Ordinal) > guard);
        foreach (var processor in new[] { "DemucsAudioStemProcessor.cs", "RemoteApiAudioStemProcessor.cs" })
        {
            var source = File.ReadAllText(Path.Combine(root, "LearnMore/Services", processor));
            var registration = source.IndexOf("await _jobService.RegisterCompletedStemsAsync", StringComparison.Ordinal);
            Assert.True(registration >= 0);
            Assert.True(source.IndexOf("Directory.CreateDirectory(destinationRoot)", StringComparison.Ordinal) > registration);
            Assert.True(source.IndexOf("File.Copy(", StringComparison.Ordinal) > registration);
        }
    }
}
