namespace OSSMDotNet.Abstractions;

/// <summary>
/// Describes whether a retained observation is current enough for live use.
/// </summary>
public enum ObservationFreshness
{
    /// <summary>
    /// No observation is available.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The observation is within the implementation's live interval.
    /// </summary>
    Live,

    /// <summary>
    /// The observation is retained but no longer live.
    /// </summary>
    LastKnown,
}