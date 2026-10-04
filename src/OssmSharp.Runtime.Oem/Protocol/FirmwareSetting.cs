namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Identifies a firmware control assigned by a transport command.
/// </summary>
internal enum FirmwareSetting
{
    /// <summary>
    /// Selects the firmware motion pattern.
    /// </summary>
    Pattern,

    /// <summary>
    /// Assigns raw velocity from zero through 100, independent of stroke.
    /// </summary>
    Speed,

    /// <summary>
    /// Assigns stroke from zero through 100 percent.
    /// </summary>
    Stroke,

    /// <summary>
    /// Assigns depth from zero through 100 percent.
    /// </summary>
    Depth,

    /// <summary>
    /// Assigns sensation from zero through 100 percent.
    /// </summary>
    Sensation,
}
