using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock;

/// <summary>
/// Identifies the Bluetooth services and characteristics of the stock OSSM firmware.
/// </summary>
internal static class StockFirmwareProfile
{
    private const string Prefix = "522b443a-4f53-534d-";
    private const string Suffix = "-420badbabe69";

    /// <summary>
    /// Gets the stock device service identifier.
    /// </summary>
    internal static readonly Guid Service = Create("0001");
    /// <summary>
    /// Gets the pairing identity characteristic identifier.
    /// </summary>
    internal static readonly Guid Identity = Create("0010");
    /// <summary>
    /// Gets the setting command characteristic identifier.
    /// </summary>
    internal static readonly Guid Command = Create("1000");
    /// <summary>
    /// Gets the knob configuration characteristic identifier.
    /// </summary>
    internal static readonly Guid KnobConfiguration = Create("1010");
    /// <summary>
    /// Gets the reported state characteristic identifier.
    /// </summary>
    internal static readonly Guid State = Create("2000");
    /// <summary>
    /// Gets the pattern list characteristic identifier.
    /// </summary>
    internal static readonly Guid Patterns = Create("3000");
    /// <summary>
    /// Gets the pattern data characteristic identifier.
    /// </summary>
    internal static readonly Guid PatternData = Create("3010");

    internal static DeviceInformation Verify(
        string peripheralId,
        Guid service,
        IReadOnlyCollection<Guid> characteristics,
        ReadOnlySpan<byte> identityPayload,
        ReadOnlySpan<byte> patternsPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peripheralId);
        ArgumentNullException.ThrowIfNull(characteristics);

        if (service != Service ||
            !characteristics.Contains(Identity) ||
            !characteristics.Contains(Command) ||
            !characteristics.Contains(KnobConfiguration) ||
            !characteristics.Contains(State) ||
            !characteristics.Contains(Patterns) ||
            !characteristics.Contains(PatternData))
        {
            throw new InvalidDataException("The required stock GATT surface is missing.");
        }

        if (identityPayload.Length is 0 or > 256 || patternsPayload.Length is 0 or > 4096)
        {
            throw new InvalidDataException("The stock identification payload exceeds its bounds.");
        }

        var identity = PairingIdentity.Parse(identityPayload);

        var patterns = PatternMetadata.Parse(patternsPayload);
        if (!patterns.Any(pattern => pattern.Index == (int)MotionPattern.SimpleStroke))
        {
            throw new InvalidDataException("The stock device does not report Simple Stroke.");
        }

        return new DeviceInformation(
            peripheralId,
            "stock-ble-1.0.34",
            identity.Version,
            [MotionPattern.SimpleStroke]);
    }

    private static Guid Create(string value) => new($"{Prefix}{value}{Suffix}");
}
