namespace OssmSharp.Device.Primitives.Tests;

public sealed class DeviceInformationTests
{
    [Fact]
    public void IdentityOnlyInformationDoesNotClaimAConnectedProfile()
    {
        var information = new DeviceInformation("selected", null, "1.0.34", [])
        {
            WifiMac = "AA:BB:CC:DD:EE:FF",
            Chip = "ESP32",
            WifiConnected = true,
            SketchMd5 = "0123456789abcdef0123456789abcdef",
        };

        Assert.Equal("selected", information.Identity);
        Assert.Equal("1.0.34", information.Version);
        Assert.Equal("AA:BB:CC:DD:EE:FF", information.WifiMac);
        Assert.Equal("ESP32", information.Chip);
        Assert.True(information.WifiConnected);
        Assert.Equal("0123456789abcdef0123456789abcdef", information.SketchMd5);
        Assert.Null(information.ProfileId);
        Assert.Empty(information.SupportedPatterns);
    }

    [Fact]
    public void ConnectedInformationRetainsProfileAndOptionalIdentityDetails()
    {
        var information = new DeviceInformation("simulator", "ossm-rs-sim-v1", null, [MotionPattern.SimpleStroke]);

        Assert.Equal("ossm-rs-sim-v1", information.ProfileId);
        Assert.Equal([MotionPattern.SimpleStroke], information.SupportedPatterns);
        Assert.Null(information.Identifier);
        Assert.Null(information.WifiMac);
        Assert.Null(information.Chip);
        Assert.Null(information.WifiConnected);
        Assert.Null(information.SketchMd5);
    }
}
