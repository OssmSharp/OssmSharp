using AwesomeAssertions;
using OssmSharp.Runtime.Oem;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class MotionSettingsTests
{
    [Fact]
    public void ExpandsSettingsWithoutPatternPolicy()
    {
        var settings = new MotionSettings(StrokePattern.Insist, 12, 0, 100, 50);

        settings.ToCommands().Should().Equal(
            new DeviceCommand(DeviceControl.Pattern, 6),
            new DeviceCommand(DeviceControl.Stroke, 0),
            new DeviceCommand(DeviceControl.Depth, 100),
            new DeviceCommand(DeviceControl.Sensation, 50),
            new DeviceCommand(DeviceControl.Speed, 12));
        new DeviceState(settings).Should().Be(new DeviceState(StrokePattern.Insist, 12, 0, 100, 50));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 0, "pattern")]
    [InlineData(7, 0, 0, 0, 0, "pattern")]
    [InlineData(0, -1, 0, 0, 0, "speed")]
    [InlineData(0, 0, 101, 0, 0, "stroke")]
    [InlineData(0, 0, 0, int.MinValue, 0, "depth")]
    [InlineData(0, 0, 0, 0, int.MaxValue, "sensation")]
    public void RejectsInvalidArgumentsBeforeSubmission(int pattern, int speed, int stroke, int depth, int sensation, string argument)
    {
        var act = () => new MotionSettings((StrokePattern)pattern, speed, stroke, depth, sensation);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(argument);
    }
}
