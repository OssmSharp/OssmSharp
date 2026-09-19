namespace OSSMDotNet.Abstractions;

/// <summary>
/// Identifies a stock OSSM motion pattern.
/// </summary>
public enum MotionPattern
{
    /// <summary>
    /// Uses the simple stroke pattern.
    /// </summary>
    SimpleStroke = 0,

    /// <summary>
    /// Uses the teasing and pounding pattern.
    /// </summary>
    TeasingPounding = 1,

    /// <summary>
    /// Uses the robotic stroke pattern.
    /// </summary>
    RoboStroke = 2,

    /// <summary>
    /// Uses the alternating half-stroke pattern.
    /// </summary>
    HalfAndHalf = 3,

    /// <summary>
    /// Uses the progressively deeper pattern.
    /// </summary>
    Deeper = 4,

    /// <summary>
    /// Uses the stop-and-go pattern.
    /// </summary>
    StopAndGo = 5,

    /// <summary>
    /// Uses the insist pattern.
    /// </summary>
    Insist = 6,
}
