namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Identifies why a settings submission finished. The state of the machine may not reflect the result if part of the command was executed.
/// </summary>
public enum SetStatus
{
    /// <summary>
    /// Every primitive write completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// A newer submission replaced commands not yet sent.
    /// </summary>
    Superseded,

    /// <summary>
    /// Cancellation, Stop, disarming, or disposal discarded commands not yet sent.
    /// </summary>
    Cancelled,
}
