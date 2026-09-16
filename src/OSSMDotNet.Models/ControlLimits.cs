namespace OSSMDotNet.Models;

/// <summary>Defines the maximum speed and depth permitted by the active control policy.</summary>
public sealed record ControlLimits(int SpeedCap, int MaximumDepth);