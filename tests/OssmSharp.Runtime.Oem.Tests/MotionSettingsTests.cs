using AwesomeAssertions;
using CsCheck;
using FastMoq;
using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class MotionSettingsTests(ITestOutputHelper output) : MockerTestBase<MotionSettings>
{
    protected override Func<Mocker, MotionSettings> CreateComponentAction => _ =>
        new(StrokePattern.Insist, 12, 0, 100, 50);

    [Fact]
    public void AllIntegerSpeedStrokePairsSatisfyIndependentFloorBounds()
    {
        var pairs = 0;
        for (var speed = 0; speed <= 100; speed++)
        {
            for (var stroke = 0; stroke <= 100; stroke++)
            {
                var commands = new MotionSettings(StrokePattern.SimpleStroke, speed, stroke, 0, 0).ToCommands();
                var velocity = commands[^1].Value;
                Assert.Equal(FirmwareSetting.Speed, commands[^1].Setting);
                Assert.True(100 * velocity <= speed * stroke);
                Assert.True(speed * stroke < 100 * (velocity + 1));
                pairs++;
            }
        }

        Assert.Equal(10201, pairs);
    }

    [Fact]
    public void GeneratedCommandsKeepPatternDepthAndSensationIndependentOfVelocity()
    {
        Gen.Int[0, 100].Array[5].Sample(
            values =>
        {
            var speed = values[0];
            var stroke = values[1];
            var pattern = (StrokePattern)(values[2] % 7);
            var commands = new MotionSettings(pattern, speed, stroke, values[3], values[4]).ToCommands();
            Assert.Equal(5, commands.Length);
            Assert.Equal(new FirmwareCommand(FirmwareSetting.Pattern, (int)pattern), commands[0]);
            Assert.Equal(new FirmwareCommand(FirmwareSetting.Stroke, stroke), commands[1]);
            Assert.Equal(new FirmwareCommand(FirmwareSetting.Depth, values[3]), commands[2]);
            Assert.Equal(new FirmwareCommand(FirmwareSetting.Sensation, values[4]), commands[3]);
            Assert.True(100 * commands[4].Value <= speed * stroke);
            Assert.True(speed * stroke < 100 * (commands[4].Value + 1));
            foreach (var alternative in Enum.GetValues<StrokePattern>())
            {
                Assert.Equal(commands[4], new MotionSettings(alternative, speed, stroke, 100 - values[3], 100 - values[4]).ToCommands()[4]);
            }
            },
            threads: 1,
            writeLine: output.WriteLine);
    }

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
        Component.ToCommands().Should().Equal(
            new FirmwareCommand(FirmwareSetting.Pattern, 6),
            new FirmwareCommand(FirmwareSetting.Stroke, 0),
            new FirmwareCommand(FirmwareSetting.Depth, 100),
            new FirmwareCommand(FirmwareSetting.Sensation, 50),
            new FirmwareCommand(FirmwareSetting.Speed, 0));
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
