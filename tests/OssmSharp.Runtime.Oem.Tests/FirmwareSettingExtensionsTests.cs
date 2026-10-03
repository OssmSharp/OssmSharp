using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class FirmwareSettingExtensionsTests
{
    [Theory]
    [InlineData((int)FirmwareSetting.Pattern, DeviceSetting.Pattern)]
    [InlineData((int)FirmwareSetting.Speed, DeviceSetting.Speed)]
    [InlineData((int)FirmwareSetting.Stroke, DeviceSetting.Stroke)]
    [InlineData((int)FirmwareSetting.Depth, DeviceSetting.Depth)]
    [InlineData((int)FirmwareSetting.Sensation, DeviceSetting.Sensation)]
    public void ConvertsSettingsInBothDirections(int firmwareValue, DeviceSetting deviceSetting)
    {
        var firmwareSetting = (FirmwareSetting)firmwareValue;

        Assert.Equal(deviceSetting, firmwareSetting.ToDeviceSetting());
        Assert.Equal(firmwareSetting, deviceSetting.ToFirmwareSetting());
        Assert.Equal(firmwareSetting, firmwareSetting.ToDeviceSetting().ToFirmwareSetting());
        Assert.Equal(deviceSetting, deviceSetting.ToFirmwareSetting().ToDeviceSetting());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void RejectsUndefinedFirmwareSetting(int value)
    {
        var setting = (FirmwareSetting)value;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => setting.ToDeviceSetting());

        Assert.Equal("setting", exception.ParamName);
        Assert.Equal(setting, exception.ActualValue);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void RejectsUndefinedDeviceSetting(int value)
    {
        var setting = (DeviceSetting)value;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => setting.ToFirmwareSetting());

        Assert.Equal("setting", exception.ParamName);
        Assert.Equal(setting, exception.ActualValue);
    }
}
