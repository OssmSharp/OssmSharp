namespace OSSMDotNet.Models;

/// <summary>Represents the control limits safe to disclose to a guest.</summary>
public sealed record GuestLimits(int SpeedCap, int MaximumDepth);