namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Identifies a requested motion control, not a motor position or a motion-safety decision.
/// </summary>
public enum DeviceSetting
{
    /// <summary>
    /// Selects the stroke pattern.
    /// </summary>
    Pattern,

    /// <summary>
    /// Identifies the requested speed as an integer percentage from zero through 100.
    /// </summary>
    /// <remarks>
    /// Uses the same units as <see cref="MotionSettings.Speed"/>; 50 means 50%.
    /// This percentage does not measure physical speed or strokes per minute.
    /// </remarks>
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
