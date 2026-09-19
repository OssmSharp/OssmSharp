namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents a state notification from an OSSM device.
/// </summary>
public sealed record DeviceState(
    DateTime Timestamp,
    MotionSettings MotionSettings,
    Guid SessionId);
