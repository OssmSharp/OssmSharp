namespace OssmSharp.Web.Client.Tests;

/// <summary>
/// Checks that snapshot revisions accept new state without replaying stale updates.
/// </summary>
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
