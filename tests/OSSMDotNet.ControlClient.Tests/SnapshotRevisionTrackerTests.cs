using OSSMDotNet.ControlClient;

namespace OSSMDotNet.ControlClient.Tests;

public sealed class SnapshotRevisionTrackerTests
{
    [Fact]
    public void AcceptsOnlyStrictlyNewerSnapshotRevisions()
    {
        var tracker = new SnapshotRevisionTracker();

        Assert.True(tracker.TryAccept(0));
        Assert.False(tracker.TryAccept(0));
        Assert.False(tracker.TryAccept(-1));
        Assert.True(tracker.TryAccept(1));
    }
}
