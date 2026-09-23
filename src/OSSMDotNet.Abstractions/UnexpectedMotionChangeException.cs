using OSSMDotNet.Abstractions.Models;

namespace OSSMDotNet.Abstractions;

/// <summary>
/// Indicates that a reported motion setting changed without a matching pending write.
/// </summary>
public sealed class UnexpectedMotionChangeException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnexpectedMotionChangeException"/> class.
    /// </summary>
    /// <param name="previousSettings">The prior reported control settings.</param>
    /// <param name="currentSettings">The newly reported control settings.</param>
    /// <param name="pendingRequestedSettings">The pending requested settings, when one existed.</param>
    /// <param name="stateTimestamp">The UTC state timestamp.</param>
    public UnexpectedMotionChangeException(
        MotionSettings previousSettings,
        MotionSettings currentSettings,
        MotionSettings? pendingRequestedSettings,
        DateTime stateTimestamp)
        : base("The reported device motion settings changed without a matching pending write.")
    {
        PreviousSettings = previousSettings;
        CurrentSettings = currentSettings;
        PendingRequestedSettings = pendingRequestedSettings;
        StateTimestamp = stateTimestamp;
    }

    /// <summary>
    /// Gets the prior reported control settings.
    /// </summary>
    public MotionSettings PreviousSettings { get; }

    /// <summary>
    /// Gets the newly reported control settings.
    /// </summary>
    public MotionSettings CurrentSettings { get; }

    /// <summary>
    /// Gets the pending requested settings, when one existed.
    /// </summary>
    public MotionSettings? PendingRequestedSettings { get; }

    /// <summary>
    /// Gets the UTC state timestamp.
    /// </summary>
    public DateTime StateTimestamp { get; }
}
