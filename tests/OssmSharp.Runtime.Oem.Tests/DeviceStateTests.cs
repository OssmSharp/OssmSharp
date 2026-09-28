using System.Text;
using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceStateTests
{
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
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ReturnsFieldErrorsForNonIntegerValues(string value)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"pattern":0,"speed":{{value}},"stroke":0,"depth":0,"sensation":0}""");

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeFalse();
        state.Should().BeNull();
        errors.Should().ContainSingle().Which.MemberNames.Should().Equal("speed");
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
        var json = """{"pattern":2147483647,"speed":0,"stroke":100,"depth":0,"sensation":100,"extra":true}""";
        var payload = Encoding.UTF8.GetBytes(json.PadRight(512));

        var success = DeviceState.TryFromBytes(payload, out var state, out var errors);

        success.Should().BeTrue();
        errors.Should().BeEmpty();
        state.Should().Be(new DeviceState(new MotionSettings(int.MaxValue, 0, 100, 0, 100)));
        DeviceState.FromBytes(payload).Should().Be(state);
    }
}
