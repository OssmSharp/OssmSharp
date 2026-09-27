namespace OssmSharp.Device.Stock;

/// <summary>
/// Chooses an actual device-reported name without adding presentation text.
/// </summary>
internal static class StockBleDeviceName
{
    internal static string Select(string? advertisedName, string? peripheralName)
    {
        if (!string.IsNullOrWhiteSpace(advertisedName))
        {
            return advertisedName;
        }

        return !string.IsNullOrWhiteSpace(peripheralName) ? peripheralName : string.Empty;
    }
}
