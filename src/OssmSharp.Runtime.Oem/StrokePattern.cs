namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Identifies a firmware stroke pattern. The firmware decides how each pattern moves and uses sensation.
/// </summary>
public enum StrokePattern
{
    /// <summary>
    /// Even strokes in and out.
    /// </summary>
    SimpleStroke = 0,

    /// <summary>
    /// Sensation shifts speed between the in and out strokes.
    /// </summary>
    TeasingPounding = 1,

    /// <summary>
    /// Sensation changes acceleration, from gentle to near-constant speed.
    /// </summary>
    RoboStroke = 2,

    /// <summary>
    /// Alternates full and half-length strokes.
    /// </summary>
    HalfNHalf = 3,

    /// <summary>
    /// Depth ramps up over a series of strokes, then restarts.
    /// </summary>
    Deeper = 4,

    /// <summary>
    /// Series of strokes separated by pauses.
    /// </summary>
    StopNGo = 5,

    /// <summary>
    /// Sensation shortens the stroke while keeping full-stroke speed.
    /// </summary>
    Insist = 6,
}
