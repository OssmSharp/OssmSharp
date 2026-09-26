namespace OssmSharp.Cli.Models;

/// <summary>
/// Carries the parsed device diagnostic command and its bounded motion settings.
/// </summary>
/// <param name="Operation">The diagnostic operation to perform.</param>
/// <param name="SimulatorPath">The optional simulator executable path.</param>
/// <param name="Speed">The requested speed.</param>
/// <param name="Stroke">The requested stroke length.</param>
/// <param name="Depth">The requested depth.</param>
/// <param name="Duration">The requested run duration.</param>
/// <param name="PeripheralId">The selected stock-device locator.</param>
/// <param name="WifiMac">The enrolled stock-device Wi-Fi MAC address.</param>
internal sealed record DeviceCommandRequest(
    DeviceCommandOperation Operation,
    string? SimulatorPath = null,
    int Speed = 0,
    int Stroke = 20,
    int Depth = 50,
    TimeSpan Duration = default,
    string? PeripheralId = null,
    string? WifiMac = null);
