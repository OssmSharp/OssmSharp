namespace OSSMDotNet.Models;

/// <summary>Defines the maximum speed and depth permitted by the active control policy.</summary>
/// <param name="SpeedCap">The maximum permitted speed.</param>
/// <param name="MaximumDepth">The maximum permitted depth.</param>
public sealed record ControlLimits(int SpeedCap, int MaximumDepth);
