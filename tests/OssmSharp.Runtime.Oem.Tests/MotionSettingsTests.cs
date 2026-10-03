using AwesomeAssertions;
using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class MotionSettingsTests
{
    [Theory]
    [InlineData(53, 10, 5)]
    [InlineData(0, 100, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(99, 1, 0)]
    [InlineData(100, 1, 1)]
    [InlineData(1, 99, 0)]
    [InlineData(1, 100, 1)]
    [InlineData(19, 10, 1)]
    [InlineData(20, 10, 2)]
    [InlineData(21, 10, 2)]
    [InlineData(100, 100, 100)]
    public void ConvertsRequestedSpeedToWholeDeviceVelocity(int speed, int stroke, int expectedDeviceVelocityPercent)
    {
        var settings = new MotionSettings(StrokePattern.SimpleStroke, speed, stroke, 100, 100);

        Assert.Equal(speed, settings.Speed);
        Assert.Equal(new FirmwareCommand(FirmwareSetting.Speed, expectedDeviceVelocityPercent), settings.ToCommands()[^1]);
    }

    [Fact]
    public void ExpandsSettingsWithoutPatternPolicy()
    {
        var settings = new MotionSettings(StrokePattern.Insist, 12, 0, 100, 50);

        settings.ToCommands().Should().Equal(
            new FirmwareCommand(FirmwareSetting.Pattern, 6),
            new FirmwareCommand(FirmwareSetting.Stroke, 0),
            new FirmwareCommand(FirmwareSetting.Depth, 100),
            new FirmwareCommand(FirmwareSetting.Sensation, 50),
            new FirmwareCommand(FirmwareSetting.Speed, 0));
        Assert.Equal(new DeviceState(StrokePattern.Insist, 0, 0, 100, 50), new DeviceState(settings));
        Assert.Null(new DeviceState(settings).Speed);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 0, "pattern")]
    [InlineData(7, 0, 0, 0, 0, "pattern")]
    [InlineData(0, -1, 0, 0, 0, "speed")]
    [InlineData(0, 101, 0, 0, 0, "speed")]
    [InlineData(0, 0, -1, 0, 0, "stroke")]
    [InlineData(0, 0, 101, 0, 0, "stroke")]
    [InlineData(0, 0, 0, int.MinValue, 0, "depth")]
    [InlineData(0, 0, 0, 0, int.MaxValue, "sensation")]
    public void RejectsInvalidArgumentsBeforeRequest(int pattern, int speed, int stroke, int depth, int sensation, string argument)
    {
        var act = () => new MotionSettings((StrokePattern)pattern, speed, stroke, depth, sensation);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(argument);
    }
}
