using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceCommandTests
{
    [Theory]
    [InlineData(DeviceControl.Pattern, 0)]
    [InlineData(DeviceControl.Pattern, 6)]
    [InlineData(DeviceControl.Speed, 0)]
    [InlineData(DeviceControl.Speed, 100)]
    [InlineData(DeviceControl.Stroke, 12)]
    [InlineData(DeviceControl.Depth, 100)]
    [InlineData(DeviceControl.Sensation, 0)]
    public void PreservesValidAssignment(DeviceControl control, int value)
    {
        var command = new DeviceCommand(control, value);

        command.Control.Should().Be(control);
        command.Value.Should().Be(value);
        command.Should().Be(new DeviceCommand(control, value));
    }

    [Theory]
    [InlineData((DeviceControl)99, 0)]
    [InlineData(DeviceControl.Pattern, -1)]
    [InlineData(DeviceControl.Pattern, 7)]
    [InlineData(DeviceControl.Pattern, int.MaxValue)]
    [InlineData(DeviceControl.Speed, -1)]
    [InlineData(DeviceControl.Stroke, 101)]
    [InlineData(DeviceControl.Depth, int.MinValue)]
    [InlineData(DeviceControl.Sensation, int.MaxValue)]
    public void RejectsInvalidAssignment(DeviceControl control, int value)
    {
        var act = () => new DeviceCommand(control, value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
