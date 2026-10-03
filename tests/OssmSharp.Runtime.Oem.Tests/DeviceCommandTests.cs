using AwesomeAssertions;
using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceCommandTests
{
    [Theory]
    [InlineData((int)FirmwareSetting.Pattern, 0)]
    [InlineData((int)FirmwareSetting.Pattern, 6)]
    [InlineData((int)FirmwareSetting.Speed, 0)]
    [InlineData((int)FirmwareSetting.Speed, 53)]
    [InlineData((int)FirmwareSetting.Speed, 100)]
    [InlineData((int)FirmwareSetting.Stroke, 12)]
    [InlineData((int)FirmwareSetting.Depth, 100)]
    [InlineData((int)FirmwareSetting.Sensation, 0)]
    public void PreservesValidAssignment(int controlValue, int value)
    {
        var control = (FirmwareSetting)controlValue;
        var command = new FirmwareCommand(control, value);

        command.Setting.Should().Be(control);
        command.Value.Should().Be(value);
        command.Should().Be(new FirmwareCommand(control, value));
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData((int)FirmwareSetting.Pattern, -1)]
    [InlineData((int)FirmwareSetting.Pattern, 7)]
    [InlineData((int)FirmwareSetting.Pattern, int.MaxValue)]
    [InlineData((int)FirmwareSetting.Speed, -1)]
    [InlineData((int)FirmwareSetting.Speed, 101)]
    [InlineData((int)FirmwareSetting.Stroke, 101)]
    [InlineData((int)FirmwareSetting.Depth, int.MinValue)]
    [InlineData((int)FirmwareSetting.Sensation, int.MaxValue)]
    public void RejectsInvalidAssignment(int controlValue, int value)
    {
        var control = (FirmwareSetting)controlValue;
        var act = () => new FirmwareCommand(control, value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
