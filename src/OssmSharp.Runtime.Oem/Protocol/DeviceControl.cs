namespace OssmSharp.Runtime.Oem.Protocol;

internal enum DeviceControl
{
    Pattern,

    /// <summary>
    /// Assigns raw velocity from zero through 100, independent of stroke.
    /// </summary>
    Speed,

    Stroke,
    Depth,
    Sensation,
}
