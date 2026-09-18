namespace OSSMDotNet.ControlClient;

/// <summary>Accepts each control-service snapshot revision at most once and in ascending order.</summary>
public sealed class SnapshotRevisionTracker
{
    private long _lastAccepted = -1;

    /// <summary>Returns whether the supplied revision is newer than the most recently accepted revision.</summary>
    public bool TryAccept(long revision)
    {
        if (revision <= _lastAccepted)
            return false;

        _lastAccepted = revision;
        return true;
    }
}
