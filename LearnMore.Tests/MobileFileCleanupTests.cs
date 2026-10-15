using LearnMore.Services;
using LearnMore.Services.Mobile;
using Xunit;

namespace LearnMore.Tests;

public class MobileFileCleanupTests
{
    [Fact]
    public async Task FileFailureKeepsJobPendingForRetry()
    {
        var acknowledged = false;
        Task Acknowledge() { acknowledged = true; return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => MobileFileCleanupService.CompleteDeletionAsync(
            () => throw new IOException("synthetic file failure"), Acknowledge, default));
        Assert.False(acknowledged);
        await MobileFileCleanupService.CompleteDeletionAsync(() => { }, Acknowledge, default);
        Assert.True(acknowledged);
    }

    [Fact]
    public async Task AcknowledgementRetryAndLateWorkerDoNotRecreateDeletedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "learnmore-cleanup-test-" + Guid.NewGuid());
        var file = Path.Combine(root, "synthetic.flac");
        var attempts = 0;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, "synthetic fixture");
            void DeleteFiles() { if (Directory.Exists(root)) Directory.Delete(root, true); }
            Task Acknowledge() => ++attempts == 1 ? Task.FromException(new IOException("synthetic acknowledgement failure")) : Task.CompletedTask;
            await Assert.ThrowsAsync<IOException>(() => MobileFileCleanupService.CompleteDeletionAsync(DeleteFiles, Acknowledge, default));
            Assert.False(Directory.Exists(root));
            var published = await AudioStemPublicationGuard.PublishAsync(() => Task.FromResult(false), () =>
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(file, "late synthetic fixture");
            });
            Assert.False(published);
            await MobileFileCleanupService.CompleteDeletionAsync(DeleteFiles, Acknowledge, default);
            Assert.False(Directory.Exists(root));
            Assert.Equal(2, attempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancellationCannotAcknowledgeUnfinishedDeletion()
    {
        using var cancellation = new CancellationTokenSource();
        var acknowledged = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => MobileFileCleanupService.CompleteDeletionAsync(
            cancellation.Cancel, () => { acknowledged = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.False(acknowledged);
    }
}
