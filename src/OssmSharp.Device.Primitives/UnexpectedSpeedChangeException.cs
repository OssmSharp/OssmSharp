namespace OssmSharp.Device.Primitives;

/// <summary>
/// Indicates that a reported speed change was not explained by the pending write.
/// </summary>
public sealed class UnexpectedSpeedChangeException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnexpectedSpeedChangeException"/> class.
    /// </summary>
    /// <param name="previousStateSpeed">The previous reported effective speed.</param>
    /// <param name="stateSpeed">The newly reported effective speed.</param>
    /// <param name="pendingRequestedSpeed">The pending requested speed, when one existed.</param>
    /// <param name="stateTimestamp">The UTC state timestamp.</param>
    /// <param name="innerException">The provider failure associated with the state, when present.</param>
    public UnexpectedSpeedChangeException(
        int previousStateSpeed,
        int stateSpeed,
        int? pendingRequestedSpeed,
        DateTime stateTimestamp,
        Exception? innerException = null)
        : base("The reported device speed changed without a matching pending write.", innerException)
    {
        PreviousStateSpeed = previousStateSpeed;
        StateSpeed = stateSpeed;
        PendingRequestedSpeed = pendingRequestedSpeed;
        StateTimestamp = stateTimestamp;
    }

    /// <summary>
    /// Gets the previous reported effective speed.
    /// </summary>
    public int PreviousStateSpeed { get; }

    /// <summary>
    /// Gets the newly reported effective speed.
    /// </summary>
    public int StateSpeed { get; }

    /// <summary>
    /// Gets the pending requested speed, when one existed.
    /// </summary>
    public int? PendingRequestedSpeed { get; }

    /// <summary>
    /// Gets the UTC state timestamp.
    /// </summary>
    public DateTime StateTimestamp { get; }
}
