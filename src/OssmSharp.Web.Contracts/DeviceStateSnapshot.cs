namespace OssmSharp.Web.Contracts;

/// <summary>
/// Projects the runtime's last known device controls.
/// </summary>
/// <param name="Pattern">The observed runtime pattern value, if available.</param>
/// <param name="Speed">The speed calculated by the runtime from velocity and stroke in the same device snapshot. Preserves decimals and values above 100%; null when unavailable or device stroke is zero. Not physical speed or strokes per minute.</param>
/// <param name="Stroke">The observed stroke percentage, if available.</param>
/// <param name="Depth">The observed depth percentage, if available.</param>
/// <param name="Sensation">The observed sensation percentage, if available.</param>
/// <param name="IsArmed">Whether the runtime accepts settings.</param>
/// <param name="ConnectionFailed">Whether the runtime recorded a connection failure.</param>
/// <param name="IsConnected">Whether connection setup completed and the connection remains open.</param>
/// <param name="IsConnecting">Whether the session is selecting or connecting a device.</param>
/// <param name="Name">The selected device name when disclosed to the user.</param>
/// <param name="Identifier">The selected device identifier when disclosed to the user.</param>
/// <param name="ConnectedAt">When the current successful connection completed, if known.</param>
/// <param name="TransmittedSettings">The last successfully sent requested selection disclosed to the user, not raw firmware velocity or observed motion.</param>
/// <param name="IsResettingPosition">Whether a one-shot position reset is still running or cleaning up. Stop remains available.</param>
public sealed record DeviceStateSnapshot(
    int? Pattern,
    double? Speed,
    int? Stroke,
    int? Depth,
    int? Sensation,
    bool IsArmed,
    bool ConnectionFailed,
    bool IsConnected = false,
    bool IsConnecting = false,
    string? Name = null,
    string? Identifier = null,
    DateTimeOffset? ConnectedAt = null,
    DeviceSettingsSnapshot? TransmittedSettings = null,
    bool IsResettingPosition = false);
