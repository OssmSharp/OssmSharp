using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

public sealed class StockQualifiedMappingTests
{
    private static readonly DeviceInformation Verified = new(
        "enrolled", "stock-ble-1.0.34", "1.0.34", [MotionPattern.SimpleStroke]);
    private static readonly PairingIdentity Identity = new(
        "A4:CF:12:34:56:78", "ESP32-S3", false, "0123456789abcdef0123456789abcdef", "1.0.34");

    [Fact]
    public void ExactEndpointsAreAcceptedWithoutClampingOrRounding()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 25, 75, 100);
        mapping.VerifyConnection(Verified, Identity);
        var requested = Settings(50, 75);

        var mapped = mapping.ValidateAndMap(requested);

        Assert.Same(requested, mapped.Settings);
    }

    [Theory]
    [InlineData(61, 80)]
    [InlineData(20, 81)]
    public void OutOfBoundsEndpointsAreRejected(int stroke, int depth)
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 25, 75, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(stroke, depth)));
    }

    [Fact]
    public void FractionalMillimetersOnTheFirmwareStepGridAreAccepted()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 101, 0, 101, 100);
        var requested = Settings(25, 25);

        Assert.Same(requested, mapping.ValidateAndMap(requested).Settings);
    }

    [Fact]
    public void EndpointBetweenFirmwareStepsIsRejectedBeforeTruncation()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 101, 0, 101, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(20, 41)));
    }

    [Fact]
    public void FloatConversionCannotTruncateAnOtherwiseAlignedEndpoint()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 101, 0, 101, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(20, 30)));
    }

    [Fact]
    public void CalibrationBetweenFirmwareStepsCannotCreateAMapping()
    {
        Assert.Throws<ArgumentException>(() =>
            new StockQualifiedMapping(Verified, Identity, 100.03m, 0, 100, 100));
    }

    [Fact]
    public void FirmwareKeepoutCannotBeBypassedByAQualifiedEnvelope()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(20, 100)));
    }

    [Fact]
    public void InvalidTravelCannotCreateAQualifiedMapping()
    {
        Assert.Throws<ArgumentException>(() =>
            new StockQualifiedMapping(Verified, Identity, 100, 80, 20, 100));
        Assert.Throws<ArgumentException>(() =>
            new StockQualifiedMapping(Verified, Identity, 0, 0, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 101));
    }

    [Fact]
    public void ReportedPatternDoesNotImplyQualifiedPattern()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(20, 40) with { Pattern = MotionPattern.TeasingPounding }));
    }

    [Fact]
    public void ConnectionIdentityMustMatchTheResolvedMapping()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 100);

        Assert.Throws<InvalidOperationException>(() =>
            mapping.VerifyConnection(Verified with { Version = "1.0.35" }, Identity));
        Assert.Throws<InvalidOperationException>(() =>
            mapping.VerifyConnection(Verified with { SupportedPatterns = [MotionPattern.TeasingPounding] }, Identity));
        Assert.Throws<InvalidOperationException>(() =>
            mapping.VerifyConnection(Verified, Identity with { SketchMd5 = new string('0', 32) }));
    }

    [Fact]
    public void EquivalentVerifiedProfileAndFirmwareAcceptFreshInstancesAndWifiStatus()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 100);
        var reverified = new DeviceInformation(
            "new-platform-locator", Verified.ProfileId, Verified.Version, [MotionPattern.SimpleStroke]);

        mapping.VerifyConnection(reverified, Identity with { WifiConnected = !Identity.WifiConnected });
    }

    [Fact]
    public void MotionSpeedCannotExceedTheQualifiedCeiling()
    {
        var mapping = new StockQualifiedMapping(Verified, Identity, 100, 0, 100, 12);

        Assert.Equal(12, mapping.ValidateAndMap(Settings(25, 50)).Settings.Speed);
        Assert.Throws<InvalidOperationException>(() =>
            mapping.ValidateAndMap(Settings(25, 50) with { Speed = 13 }));
    }

    private static MotionSettings Settings(int stroke, int depth) =>
        new(MotionDeviceState.StrokeEngineIdle, 12, stroke, 50, depth, 0, MotionPattern.SimpleStroke, 0);
}
