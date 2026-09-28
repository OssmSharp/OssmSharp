using AwesomeAssertions;
using OssmSharp.Device.Primitives;
using Xunit;

namespace OssmSharp.Device.Runtime.Tests;

public sealed class MotionSettingsTests
{
    [Fact]
    public void ExpandsSettingsWithoutPatternPolicy()
    {
        var settings = new MotionSettings(int.MaxValue, 12.5, 0, 100, 50);

        settings.ToCommands().Should().Equal(
            new DeviceCommand(DeviceControl.Pattern, int.MaxValue),
            new DeviceCommand(DeviceControl.Stroke, 0),
            new DeviceCommand(DeviceControl.Depth, 100),
            new DeviceCommand(DeviceControl.Sensation, 50),
            new DeviceCommand(DeviceControl.Speed, 12.5));
        new DeviceState(settings).Settings.Should().Be(settings);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 0, "pattern")]
    [InlineData(0, -0.1, 0, 0, 0, "speed")]
    [InlineData(0, 0, 100.1, 0, 0, "stroke")]
    [InlineData(0, 0, 0, double.NaN, 0, "depth")]
    [InlineData(0, 0, 0, 0, double.PositiveInfinity, "sensation")]
    public void RejectsInvalidArgumentsBeforeSubmission(int pattern, double speed, double stroke, double depth, double sensation, string argument)
    {
        var act = () => new MotionSettings(pattern, speed, stroke, depth, sensation);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(argument);
    }
}
