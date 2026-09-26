namespace OssmSharp.Web.Client.Models;

/// <summary>Accepts each control-service snapshot revision at most once and in ascending order.</summary>
public sealed class SnapshotRevisionTracker
{
    private long _lastAccepted = -1;

    /// <summary>
    /// Accepts a snapshot only when its revision advances past the last accepted revision.
    /// </summary>
    /// <param name="revision">The service-owned snapshot ordering revision.</param>
    /// <returns>Whether this revision was accepted.</returns>
    public bool TryAccept(long revision)
    {
        if (revision <= _lastAccepted)
            return false;

        _lastAccepted = revision;
        return true;
    }
}
