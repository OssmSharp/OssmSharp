namespace OssmSharp.Device.Primitives.Models;

/// <summary>
/// Describes reported device identity and, when verified, its connected motion profile.
/// </summary>
/// <param name="Identity">The implementation-specific device identity.</param>
/// <param name="ProfileId">The verified profile, or null for identity-only information.</param>
/// <param name="Version">The reported firmware or simulator version, when available.</param>
/// <param name="SupportedPatterns">The supported motion patterns; empty when no profile has been verified.</param>
public sealed record DeviceInformation(
    string Identity,
    string? ProfileId,
    string? Version,
    IReadOnlyList<MotionPattern> SupportedPatterns)
{
    /// <summary>
    /// Gets the original provider selection, when information was obtained for a discovered identifier.
    /// </summary>
    public DeviceIdentifier? Identifier { get; init; }

    /// <summary>
    /// Gets the reported Wi-Fi MAC, when available.
    /// </summary>
    public string? WifiMac { get; init; }

    /// <summary>
    /// Gets the reported chip, when available.
    /// </summary>
    public string? Chip { get; init; }

    /// <summary>
    /// Gets the reported Wi-Fi connection state, when available.
    /// </summary>
    public bool? WifiConnected { get; init; }

    /// <summary>
    /// Gets the reported sketch digest, when available.
    /// </summary>
    public string? SketchMd5 { get; init; }
}
