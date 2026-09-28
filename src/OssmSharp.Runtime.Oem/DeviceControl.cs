namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Identifies a firmware setting, not a motor position or a motion-safety decision.
/// </summary>
public enum DeviceControl
{
    /// <summary>
    /// Selects the firmware pattern by its nonnegative integer identifier.
    /// </summary>
    Pattern,

    /// <summary>
    /// Assigns the speed argument as a percentage.
    /// </summary>
    Speed,

    /// <summary>
    /// Assigns the stroke argument as a percentage.
    /// </summary>
    Stroke,

    /// <summary>
    /// Assigns the depth argument as a percentage.
    /// </summary>
    Depth,

    /// <summary>
    /// Assigns the sensation argument as a percentage.
    /// </summary>
    Sensation,
}
