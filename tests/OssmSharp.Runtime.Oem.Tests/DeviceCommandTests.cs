using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceCommandTests
{
    [Theory]
    [InlineData((int)DeviceSetting.Pattern, 0)]
    [InlineData((int)DeviceSetting.Pattern, 6)]
    [InlineData((int)DeviceSetting.Speed, 0)]
    [InlineData((int)DeviceSetting.Speed, 53)]
    [InlineData((int)DeviceSetting.Speed, 100)]
    [InlineData((int)DeviceSetting.Stroke, 12)]
    [InlineData((int)DeviceSetting.Depth, 100)]
    [InlineData((int)DeviceSetting.Sensation, 0)]
    public void PreservesValidAssignment(int controlValue, int value)
    {
        var control = (DeviceSetting)controlValue;
        var command = new DeviceCommand(control, value);

        command.Control.Should().Be(control);
        command.Value.Should().Be(value);
        command.Should().Be(new DeviceCommand(control, value));
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData((int)DeviceSetting.Pattern, -1)]
    [InlineData((int)DeviceSetting.Pattern, 7)]
    [InlineData((int)DeviceSetting.Pattern, int.MaxValue)]
    [InlineData((int)DeviceSetting.Speed, -1)]
    [InlineData((int)DeviceSetting.Speed, 101)]
    [InlineData((int)DeviceSetting.Stroke, 101)]
    [InlineData((int)DeviceSetting.Depth, int.MinValue)]
    [InlineData((int)DeviceSetting.Sensation, int.MaxValue)]
    public void RejectsInvalidAssignment(int controlValue, int value)
    {
        var control = (DeviceSetting)controlValue;
        var act = () => new DeviceCommand(control, value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
