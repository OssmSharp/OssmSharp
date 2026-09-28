namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Identifies why a settings submission finished. None of these outcomes verifies physical motion.
/// </summary>
public enum SettingsOutcome
{
    /// <summary>
    /// Every primitive write completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// A newer submission replaced unissued commands.
    /// </summary>
    Superseded,

    /// <summary>
    /// Cancellation, Stop, disarming, or disposal discarded unissued commands.
    /// </summary>
    Cancelled,
}
