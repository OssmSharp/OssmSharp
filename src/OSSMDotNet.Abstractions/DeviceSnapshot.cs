namespace OSSMDotNet.Abstractions;

/// <summary>
/// Represents immutable device facts retained by an <see cref="IOssmDevice"/>.
/// </summary>
/// <param name="CapturedAtUtc">The UTC time at which this snapshot was created.</param>
/// <param name="ConnectionState">The implementation connection state.</param>
/// <param name="Device">The discovered device information, when available.</param>
/// <param name="RequestedMotion">The last motion settings submitted successfully to the transport.</param>
/// <param name="ObservationFreshness">The freshness of <paramref name="Observation"/>.</param>
/// <param name="Observation">The latest validated observation, when available.</param>
/// <param name="ObservedVelocityMillimetresPerSecond">Directly observed velocity, when the implementation supplies it.</param>
public sealed record DeviceSnapshot(
    DateTime CapturedAtUtc,
    DeviceConnectionState ConnectionState,
    DeviceInformation? Device,
    MotionSettings? RequestedMotion,
    ObservationFreshness ObservationFreshness,
    DeviceState? Observation,
    double? ObservedVelocityMillimetresPerSecond);