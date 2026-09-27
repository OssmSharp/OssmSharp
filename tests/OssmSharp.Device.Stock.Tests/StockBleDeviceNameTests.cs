namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Ensures discovery preserves unavailable names for read-only device details.
/// </summary>
public sealed class StockBleDeviceNameTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "\t")]
    public void SelectReturnsEmptyWhenNeitherSourceHasAName(
        string? advertisedName,
        string? peripheralName)
    {
        Assert.Equal(string.Empty, StockBleDeviceName.Select(advertisedName, peripheralName));
    }

    [Theory]
    [InlineData("Advertised", "Known", "Advertised")]
    [InlineData(null, "Known", "Known")]
    [InlineData("", "Known", "Known")]
    [InlineData("Advertised", null, "Advertised")]
    public void SelectPreservesAvailableNameWithoutPresentationFallback(
        string? advertisedName,
        string? peripheralName,
        string expected)
    {
        Assert.Equal(expected, StockBleDeviceName.Select(advertisedName, peripheralName));
    }
}
