using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceCommandTests
{
    [Theory]
    [InlineData((int)DeviceControl.Pattern, 0)]
    [InlineData((int)DeviceControl.Pattern, 6)]
    [InlineData((int)DeviceControl.Speed, 0)]
    [InlineData((int)DeviceControl.Speed, 53)]
    [InlineData((int)DeviceControl.Speed, 100)]
    [InlineData((int)DeviceControl.Stroke, 12)]
    [InlineData((int)DeviceControl.Depth, 100)]
    [InlineData((int)DeviceControl.Sensation, 0)]
    public void PreservesValidAssignment(int controlValue, int value)
    {
        var control = (DeviceControl)controlValue;
        var command = new DeviceCommand(control, value);

        command.Control.Should().Be(control);
        command.Value.Should().Be(value);
        command.Should().Be(new DeviceCommand(control, value));
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData((int)DeviceControl.Pattern, -1)]
    [InlineData((int)DeviceControl.Pattern, 7)]
    [InlineData((int)DeviceControl.Pattern, int.MaxValue)]
    [InlineData((int)DeviceControl.Speed, -1)]
    [InlineData((int)DeviceControl.Speed, 101)]
    [InlineData((int)DeviceControl.Stroke, 101)]
    [InlineData((int)DeviceControl.Depth, int.MinValue)]
    [InlineData((int)DeviceControl.Sensation, int.MaxValue)]
    public void RejectsInvalidAssignment(int controlValue, int value)
    {
        var control = (DeviceControl)controlValue;
        var act = () => new DeviceCommand(control, value);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
