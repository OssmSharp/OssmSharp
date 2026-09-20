namespace OSSMDotNet.Abstractions;

/// <summary>
/// Describes discovered device identity and supported motion patterns.
/// </summary>
/// <param name="Identity">The implementation-specific device identity.</param>
/// <param name="ProfileId">The profile that defines the reported semantics.</param>
/// <param name="Version">The reported firmware or simulator version, when available.</param>
/// <param name="SupportedPatterns">The motion patterns reported by the implementation.</param>
public sealed record DeviceInformation(
    string Identity,
    string ProfileId,
    string? Version,
    IReadOnlyList<MotionPattern> SupportedPatterns);
