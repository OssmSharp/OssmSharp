using System.ComponentModel.DataAnnotations;

namespace OssmSharp.Device.Stock;

/// <summary>
/// Identifies the selected physical stock device for connection.
/// </summary>
public sealed class StockBleOptions
{
    /// <summary>Gets or sets the host-specific selected peripheral locator.</summary>
    [Required]
    public string PeripheralId { get; set; } = string.Empty;
}
