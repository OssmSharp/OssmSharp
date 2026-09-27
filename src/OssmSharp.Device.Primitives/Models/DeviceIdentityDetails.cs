namespace OssmSharp.Device.Primitives.Models;

/// <summary>Descriptive identity read from a selected device, independently of its connected motion profile.</summary>
/// <param name="Identifier">The original selected identifier.</param>
/// <param name="WifiMac">The reported Wi-Fi MAC, if available.</param>
/// <param name="Chip">The reported chip, if available.</param>
/// <param name="WifiConnected">The reported Wi-Fi connection state, if available.</param>
/// <param name="SketchMd5">The reported sketch digest, if available.</param>
/// <param name="FirmwareVersion">The reported firmware version, if available.</param>
public sealed record DeviceIdentityDetails(
    DeviceIdentifier Identifier,
    string? WifiMac,
    string? Chip,
    bool? WifiConnected,
    string? SketchMd5,
    string? FirmwareVersion);
