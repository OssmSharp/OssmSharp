using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Device.Stock;

/// <summary>
/// Identifies the enrolled physical stock device for read-only connection.
/// </summary>
public sealed class StockBleOptions
{
    /// <summary>Gets or sets the host-specific enrolled peripheral locator.</summary>
    [Required]
    public string PeripheralId { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected Wi-Fi MAC from the live pairing characteristic.</summary>
    [Required]
    [RegularExpression("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")]
    public string ExpectedWifiMac { get; set; } = string.Empty;
}