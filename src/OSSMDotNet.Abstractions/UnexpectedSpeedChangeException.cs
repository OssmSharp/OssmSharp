namespace OSSMDotNet.Abstractions;

/// <summary>
/// Indicates that an observed speed change was not explained by the pending write.
/// </summary>
public sealed class UnexpectedSpeedChangeException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnexpectedSpeedChangeException"/> class.
    /// </summary>
    /// <param name="previousObservedSpeed">The previous observed effective speed.</param>
    /// <param name="observedSpeed">The newly observed effective speed.</param>
    /// <param name="pendingRequestedSpeed">The pending requested speed, when one existed.</param>
    /// <param name="observationTimestamp">The UTC observation timestamp.</param>
    /// <param name="innerException">The provider failure associated with the observation, when present.</param>
    public UnexpectedSpeedChangeException(
        int previousObservedSpeed,
        int observedSpeed,
        int? pendingRequestedSpeed,
        DateTime observationTimestamp,
        Exception? innerException = null)
        : base("The observed device speed changed without a matching pending write.", innerException)
    {
        PreviousObservedSpeed = previousObservedSpeed;
        ObservedSpeed = observedSpeed;
        PendingRequestedSpeed = pendingRequestedSpeed;
        ObservationTimestamp = observationTimestamp;
    }

    /// <summary>
    /// Gets the previous observed effective speed.
    /// </summary>
    public int PreviousObservedSpeed { get; }

    /// <summary>
    /// Gets the newly observed effective speed.
    /// </summary>
    public int ObservedSpeed { get; }

    /// <summary>
    /// Gets the pending requested speed, when one existed.
    /// </summary>
    public int? PendingRequestedSpeed { get; }

    /// <summary>
    /// Gets the UTC observation timestamp.
    /// </summary>
    public DateTime ObservationTimestamp { get; }
}