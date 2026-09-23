namespace OSSMDotNet;

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

    private static Guid Create(string value) => new($"{Prefix}{value}{Suffix}");
}
