using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OSSMDotNet.Tests;

public sealed class StockFirmwareProtocolTests
{
    private static readonly DateTime ReceiptTime = new(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);

    public static TheoryData<string, MotionDeviceState> SupportedStates => new()
    {
        { "idle", MotionDeviceState.Idle },
        { "homing", MotionDeviceState.Homing },
        { "homing.forward", MotionDeviceState.HomingForward },
        { "homing.backward", MotionDeviceState.HomingBackward },
        { "menu", MotionDeviceState.Menu },
        { "menu.idle", MotionDeviceState.MenuIdle },
        { "simplePenetration", MotionDeviceState.SimplePenetration },
        { "simplePenetration.preflight", MotionDeviceState.SimplePenetrationPreflight },
        { "simplePenetration.idle", MotionDeviceState.SimplePenetrationIdle },
        { "strokeEngine", MotionDeviceState.StrokeEngine },
        { "strokeEngine.preflight", MotionDeviceState.StrokeEnginePreflight },
        { "strokeEngine.idle", MotionDeviceState.StrokeEngineIdle },
        { "strokeEngine.pattern", MotionDeviceState.StrokeEnginePattern },
        { "streaming", MotionDeviceState.Streaming },
        { "streaming.preflight", MotionDeviceState.StreamingPreflight },
        { "streaming.idle", MotionDeviceState.StreamingIdle },
        { "pairing", MotionDeviceState.Pairing },
        { "pairing.idle", MotionDeviceState.PairingIdle },
        { "pairing.success", MotionDeviceState.PairingSuccess },
        { "pairing.success.idle", MotionDeviceState.PairingSuccessIdle },
        { "pairing.wifi", MotionDeviceState.PairingWifi },
        { "pairing.wifi.idle", MotionDeviceState.PairingWifiIdle },
        { "update", MotionDeviceState.Update },
        { "update.checking", MotionDeviceState.UpdateChecking },
        { "update.idle", MotionDeviceState.UpdateIdle },
        { "wifi", MotionDeviceState.Wifi },
        { "wifi.idle", MotionDeviceState.WifiIdle },
        { "help", MotionDeviceState.Help },
        { "help.idle", MotionDeviceState.HelpIdle },
        { "error", MotionDeviceState.Error },
        { "error.idle", MotionDeviceState.ErrorIdle },
        { "error.help", MotionDeviceState.ErrorHelp },
        { "restart", MotionDeviceState.Restart },
    };

    public static TheoryData<int, MotionPattern> SupportedPatterns => new()
    {
        { 0, MotionPattern.SimpleStroke },
        { 1, MotionPattern.TeasingPounding },
        { 2, MotionPattern.RoboStroke },
        { 3, MotionPattern.HalfAndHalf },
        { 4, MotionPattern.Deeper },
        { 5, MotionPattern.StopAndGo },
        { 6, MotionPattern.Insist },
    };

    [Fact]
    public void ParseMapsStatePayloadUsingCallerUtcReceipt()
    {
        var sessionId = Guid.Parse("18962d57-b42b-4e28-bcdc-090f55d04f06");
        var payload = ValidPayload();
        payload["timestamp"] = uint.MaxValue;
        payload["sessionId"] = sessionId.ToString();

        var state = StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime);

        Assert.Equal(ReceiptTime, state.Timestamp);
        Assert.Equal(DateTimeKind.Utc, state.Timestamp.Kind);
        Assert.Equal(sessionId, state.SessionId);
        Assert.Null(state.VelocityMillimetresPerSecond);
        Assert.Equal(
            new MotionSettings(
                MotionDeviceState.StrokeEngineIdle,
                30,
                20,
                50,
                40,
                10,
                MotionPattern.SimpleStroke,
                12.5),
            state.MotionSettings);
    }

    [Theory]
    [MemberData(nameof(SupportedStates))]
    public void ParseMapsEverySupportedState(string firmwareState, MotionDeviceState expected)
    {
        var payload = ValidPayload();
        payload["state"] = firmwareState;

        var state = StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime);

        Assert.Equal(expected, state.MotionSettings.State);
    }

    [Theory]
    [MemberData(nameof(SupportedPatterns))]
    public void ParseMapsEverySupportedPattern(int firmwarePattern, MotionPattern expected)
    {
        var payload = ValidPayload();
        payload["pattern"] = firmwarePattern;

        var state = StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime);

        Assert.Equal(expected, state.MotionSettings.Pattern);
    }

    [Fact]
    public void ParseMapsEmptySessionToGuidEmpty()
    {
        var state = StockFirmwareStateParser.Parse(ValidPayload().ToJsonString(), ReceiptTime);

        Assert.Equal(Guid.Empty, state.SessionId);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("state")]
    [InlineData("speed")]
    [InlineData("stroke")]
    [InlineData("sensation")]
    [InlineData("depth")]
    [InlineData("buffer")]
    [InlineData("pattern")]
    [InlineData("position")]
    [InlineData("sessionId")]
    public void ParseRejectsMissingRequiredField(string field)
    {
        var payload = ValidPayload();
        payload.Remove(field);

        Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));
    }

    [Fact]
    public void ParseRejectsUnknownFieldBeforeMapping()
    {
        var payload = ValidPayload();
        payload["state"] = "unsupported";
        payload["unexpected"] = true;

        var exception = Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));

        Assert.Equal("The state field 'unexpected' is unsupported.", exception.Message);
    }

    [Fact]
    public void ParseRejectsDuplicateFieldBeforeMapping()
    {
        var payload = ValidPayload().ToJsonString();
        payload = payload.Insert(payload.LastIndexOf('}'), ",\"state\":\"unsupported\"");

        var exception = Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload, ReceiptTime));

        Assert.Equal("The state field 'state' is duplicated.", exception.Message);
    }

    [Fact]
    public void ParseRejectsMalformedJsonAndPreservesCause()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse("{", ReceiptTime));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("timestamp", "-1")]
    [InlineData("timestamp", "4294967296")]
    [InlineData("speed", "-1")]
    [InlineData("speed", "101")]
    [InlineData("stroke", "-1")]
    [InlineData("stroke", "101")]
    [InlineData("sensation", "-1")]
    [InlineData("sensation", "101")]
    [InlineData("depth", "-1")]
    [InlineData("depth", "101")]
    [InlineData("buffer", "-1")]
    [InlineData("buffer", "101")]
    [InlineData("pattern", "-1")]
    [InlineData("pattern", "7")]
    [InlineData("position", "1e999")]
    [InlineData("speed", "1.5")]
    public void ParseRejectsOutOfRangeNonfiniteOrMalformedValue(string field, string value)
    {
        var payload = ValidPayload();
        payload[field] = JsonNode.Parse(value);

        Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));
    }

    [Fact]
    public void ParseRejectsUnsupportedState()
    {
        var payload = ValidPayload();
        payload["state"] = "strokeEngine.unknown";

        Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));
    }

    [Theory]
    [InlineData(1, 0, 40)]
    [InlineData(0, 41, 40)]
    public void ParseRejectsInvalidMotionSettingsAndPreservesCause(int speed, int stroke, int depth)
    {
        var payload = ValidPayload();
        payload["speed"] = speed;
        payload["stroke"] = stroke;
        payload["depth"] = depth;

        var exception = Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));

        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public void ParseRejectsMalformedSessionAndPreservesCause()
    {
        var payload = ValidPayload();
        payload["sessionId"] = "not-a-guid";

        var exception = Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(payload.ToJsonString(), ReceiptTime));

        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void ParseRejectsNonUtcReceipt()
    {
        Assert.Throws<InvalidDataException>(() =>
            StockFirmwareStateParser.Parse(ValidPayload().ToJsonString(), DateTime.UnixEpoch.ToLocalTime()));
    }

    [Fact]
    public void MapReturnsDeterministicSetCommandsIncludingZeroSpeed()
    {
        var settings = new MotionSettings(
            MotionDeviceState.Restart,
            0,
            20,
            30,
            40,
            50,
            MotionPattern.Insist,
            12.5);

        var commands = StockFirmwareCommandMapper.Map(settings);

        Assert.Equal(
            [
                "set:speed:0",
                "set:stroke:20",
                "set:sensation:30",
                "set:depth:40",
                "set:buffer:50",
                "set:pattern:6",
            ],
            commands);
        Assert.DoesNotContain(commands, command => command.StartsWith("go:", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("position", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("state", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(999, 10, 20, 40, 0, 0d)]
    [InlineData((int)MotionDeviceState.StrokeEngineIdle, 101, 20, 40, 0, 0d)]
    [InlineData((int)MotionDeviceState.StrokeEngineIdle, 10, 0, 40, 0, 0d)]
    [InlineData((int)MotionDeviceState.StrokeEngineIdle, 10, 41, 40, 0, 0d)]
    [InlineData((int)MotionDeviceState.StrokeEngineIdle, 10, 20, 40, 7, 0d)]
    [InlineData((int)MotionDeviceState.StrokeEngineIdle, 10, 20, 40, 0, double.NaN)]
    public void MapRejectsInvalidMotionSettingsAndPreservesCause(
        int state,
        int speed,
        int stroke,
        int depth,
        int pattern,
        double position)
    {
        var settings = new MotionSettings(
            (MotionDeviceState)state,
            speed,
            stroke,
            50,
            depth,
            10,
            (MotionPattern)pattern,
            position);

        var exception = Assert.Throws<InvalidDataException>(() => StockFirmwareCommandMapper.Map(settings));

        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public void IsExpectedAcknowledgementAcceptsExactCommand()
    {
        Assert.True(StockFirmwareNotificationParser.IsExpectedAcknowledgement(
            "ok:set:speed:0",
            "set:speed:0"));
    }

    [Theory]
    [InlineData("ok:", "")]
    [InlineData("ok", "set:speed:0")]
    [InlineData("fail:set:speed:0", "set:speed:0")]
    [InlineData("ok:xset:speed:0", "set:speed:0")]
    [InlineData("ok:set:speed:0x", "set:speed:0")]
    [InlineData("set:speed:0", "set:speed:0")]
    [InlineData("arbitrary text", "set:speed:0")]
    public void IsExpectedAcknowledgementRejectsAnythingElse(string payload, string expectedCommand)
    {
        Assert.False(StockFirmwareNotificationParser.IsExpectedAcknowledgement(payload, expectedCommand));
    }

    [Theory]
    [InlineData(null, null, 60, 30, true)]
    [InlineData(null, null, 60, 61, false)]
    [InlineData(60, 30, 40, 20, true)]
    [InlineData(60, 30, 40, 19, false)]
    [InlineData(3, 1, 5, 2, true)]
    [InlineData(60, 0, 20, 0, true)]
    [InlineData(60, 30, 0, 0, true)]
    [InlineData(60, 30, 0, 1, false)]
    public void ExpectedStateUsesOnlyKnobValuesConsistentWithPriorTruncatedState(
        int? previousRequested,
        int? previousReported,
        int requested,
        int reported,
        bool expected) =>
        Assert.Equal(
            expected,
            OssmDevice.IsExpectedStateImpl(previousRequested, previousReported, requested, reported));

    [Fact]
    public void ProtocolTypesRemainInternal()
    {
        Assert.False(typeof(StockFirmwareStateParser).IsPublic);
        Assert.False(typeof(StockFirmwareCommandMapper).IsPublic);
        Assert.False(typeof(StockFirmwareNotificationParser).IsPublic);
    }

    private static JsonObject ValidPayload() => new()
    {
        ["timestamp"] = 1234,
        ["state"] = "strokeEngine.idle",
        ["speed"] = 30,
        ["stroke"] = 20,
        ["sensation"] = 50,
        ["depth"] = 40,
        ["buffer"] = 10,
        ["pattern"] = 0,
        ["position"] = 12.5,
        ["sessionId"] = string.Empty,
    };
}