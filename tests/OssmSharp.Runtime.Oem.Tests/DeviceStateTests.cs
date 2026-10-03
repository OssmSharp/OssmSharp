using System.Text;
using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceStateTests
{
    [Theory]
    [InlineData(12.5, 25, 50.0)]
    [InlineData(12.345, 25, 49.38)]
    [InlineData(12.5, 3, 416.6666666666667)]
    [InlineData(100.0, 1, 10000.0)]
    [InlineData(0.0, 100, 0.0)]
    [InlineData(100.0, 100, 100.0)]
    [InlineData(0.0, 0, null)]
    [InlineData(12.5, 0, null)]
    public void CalculatesSpeedFromFirmwareSnapshot(double currentDeviceVelocityPercent, int stroke, double? expectedSpeed)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            pattern = 0,
            speed = currentDeviceVelocityPercent,
            stroke,
            depth = 99,
            sensation = 100,
        });

        Assert.True(DeviceState.TryFromBytes(payload, out var state, out var errors));
        Assert.Empty(errors);
        Assert.Equal(expectedSpeed, state.Speed);
        Assert.Equal(currentDeviceVelocityPercent, state.CurrentDeviceVelocityPercent);
        Assert.Equal(new DeviceState(StrokePattern.SimpleStroke, currentDeviceVelocityPercent, stroke, 99, 100), state);
        Assert.Equal(expectedSpeed, DeviceState.FromBytes(payload).Speed);
    }

    [Theory]
    [InlineData(53, 10, 50.0)]
    [InlineData(99, 1, 0.0)]
    [InlineData(100, 1, 100.0)]
    [InlineData(0, 100, 0.0)]
    [InlineData(100, 0, null)]
    [InlineData(0, 0, null)]
    public void RepresentsQuantizedSettings(int speed, int stroke, double? expectedSpeed)
    {
        var state = new DeviceState(new MotionSettings(StrokePattern.SimpleStroke, speed, stroke, 0, 0));

        Assert.Equal(expectedSpeed, state.Speed);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsInvalidFirmwareVelocity(double currentDeviceVelocityPercent)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DeviceState(StrokePattern.SimpleStroke, currentDeviceVelocityPercent, 100, 0, 0));

        Assert.Equal("currentDeviceVelocityPercent", exception.ParamName);
    }

    [Fact]
    public void ReturnsIndependentFieldErrorsWithoutCreatingState()
    {
        var payload = Encoding.UTF8.GetBytes("""{"pattern":-1,"speed":101,"stroke":"50","depth":null}""");

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.SelectMany(error => error.MemberNames).Should().BeEquivalentTo("pattern", "speed", "stroke", "depth", "sensation");
        errors.Should().HaveCount(5).And.OnlyContain(error => !string.IsNullOrWhiteSpace(error.ErrorMessage));
        var parse = () => DeviceState.FromBytes(payload);
        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("{} trailing")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"extra\":[[[[[[[[[]]]]]]]]]}")]
    public void ReturnsPayloadErrorsForInvalidStructure(string? json)
    {
        var success = DeviceState.TryFromBytes(json is null ? null : Encoding.UTF8.GetBytes(json), out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal("$");
    }

    [Fact]
    public void RejectsOversizedPayload()
    {
        var success = DeviceState.TryFromBytes(new byte[513], out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal("$");
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("true")]
    [InlineData("\"20\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ReturnsFieldErrorsForInvalidPercentages(string value)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"pattern":0,"speed":{{value}},"stroke":0,"depth":0,"sensation":0}""");

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal("speed");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"1\"")]
    [InlineData("-1")]
    [InlineData("7")]
    public void ReturnsFieldErrorsForInvalidPatterns(string value)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"pattern":{{value}},"speed":0,"stroke":0,"depth":0,"sensation":0}""");

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal("pattern");
    }

    [Theory]
    [InlineData("stroke")]
    [InlineData("depth")]
    [InlineData("sensation")]
    public void RejectsFractionalPercentages(string field)
    {
        var values = new Dictionary<string, decimal> { ["pattern"] = 3, ["speed"] = 12, ["stroke"] = 25, ["depth"] = 99, ["sensation"] = 50 };
        values[field] = 12.5m;
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(values);

        DeviceState.TryFromBytes(payload, out var state, out var errors).Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal(field);
        var parse = () => DeviceState.FromBytes(payload);
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void PreservesFractionalEffectiveSpeed()
    {
        var payload = Encoding.UTF8.GetBytes("""{"pattern":3,"speed":12.5,"stroke":25,"depth":99,"sensation":50}""");

        var state = DeviceState.FromBytes(payload);

        Assert.Equal(50, state.Speed);
        Assert.Equal(new DeviceState(StrokePattern.HalfNHalf, 12.5, 25, 99, 50), state);
    }

    [Fact]
    public void ReturnsDuplicateAndRangeErrorsTogether()
    {
        var payload = Encoding.UTF8.GetBytes("""{"pattern":0,"speed":0,"speed":101,"stroke":-1,"depth":101,"sensation":-1,"extra":0,"extra":1}""");

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.SelectMany(error => error.MemberNames).Should().BeEquivalentTo("speed", "speed", "stroke", "depth", "sensation", "extra");
    }

    [Fact]
    public void ParsesBoundaryValuesAndIgnoresUnknownFields()
    {
        var json = """{"pattern":6,"speed":0,"stroke":100,"depth":0,"sensation":100,"extra":true}""";
        var payload = Encoding.UTF8.GetBytes(json.PadRight(512));

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeTrue();
        errors.Should().BeEmpty();
        state.Should().Be(new DeviceState(new MotionSettings(StrokePattern.Insist, 0, 100, 0, 100)));
        DeviceState.FromBytes(payload).Should().Be(state);
    }
}
