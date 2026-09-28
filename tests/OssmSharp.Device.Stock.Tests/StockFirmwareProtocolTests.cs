using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Verifies parsing and validation of stock firmware state, identity, pattern, and acknowledgement payloads.
/// </summary>
public sealed class StockFirmwareProtocolTests
{
    private static readonly DateTime ReceiptTime = new(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
    private static readonly StockQualifiedMapping Mapping = new(
        new DeviceInformation("test", "stock-ble-1.0.34", "1.0.34", [MotionPattern.SimpleStroke]),
        new PairingIdentity("A4:CF:12:34:56:78", "ESP32-S3", false, "0123456789abcdef0123456789abcdef", "1.0.34"),
        100, 0, 100, 100);

    /// <summary>
    /// Gets the firmware state names and their expected motion states.
    /// </summary>
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

    /// <summary>
    /// Gets the firmware pattern indices and their expected motion patterns.
    /// </summary>
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

        var response = StateResponse.Parse(payload.ToJsonString(), ReceiptTime);
        var state = response.ToDeviceState();

        Assert.Equal(uint.MaxValue, response.FirmwareTimestamp);
        Assert.Equal(ReceiptTime, response.ReceivedAtUtc);
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

        var state = ParseState(payload.ToJsonString());

        Assert.Equal(expected, state.MotionSettings.State);
    }

    [Theory]
    [MemberData(nameof(SupportedPatterns))]
    public void ParseMapsEverySupportedPattern(int firmwarePattern, MotionPattern expected)
    {
        var payload = ValidPayload();
        payload["pattern"] = firmwarePattern;

        var state = ParseState(payload.ToJsonString());

        Assert.Equal(expected, state.MotionSettings.Pattern);
    }

    [Fact]
    public void ParseMapsEmptySessionToGuidEmpty()
    {
        var state = ParseState(ValidPayload().ToJsonString());

        Assert.Equal(Guid.Empty, state.SessionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ParseAcceptsPercentageBoundaries(int percentage)
    {
        var payload = ValidPayload();
        payload["speed"] = percentage;
        payload["stroke"] = percentage;
        payload["sensation"] = percentage;
        payload["depth"] = percentage;
        payload["buffer"] = percentage;

        var settings = ParseState(payload.ToJsonString()).MotionSettings;

        Assert.Equal(percentage, settings.Speed);
        Assert.Equal(percentage, settings.Stroke);
        Assert.Equal(percentage, settings.Sensation);
        Assert.Equal(percentage, settings.Depth);
        Assert.Equal(percentage, settings.Buffer);
    }

    [Fact]
    public void ParseAcceptsZeroFirmwareTimestamp()
    {
        var payload = ValidPayload();
        payload["timestamp"] = 0;

        var response = StateResponse.Parse(payload.ToJsonString(), ReceiptTime);

        Assert.Equal(0U, response.FirmwareTimestamp);
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

        Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));
    }

    [Fact]
    public void ParseRejectsUnknownFieldBeforeMapping()
    {
        var payload = ValidPayload();
        payload["state"] = "unsupported";
        payload["unexpected"] = true;

        var exception = Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));

        Assert.Equal("The state field 'unexpected' is unsupported.", exception.Message);
    }

    [Fact]
    public void ParseRejectsDuplicateFieldBeforeMapping()
    {
        var payload = ValidPayload().ToJsonString();
        payload = payload.Insert(payload.LastIndexOf('}'), ",\"state\":\"unsupported\"");

        var exception = Assert.Throws<InvalidDataException>(() => ParseState(payload));

        Assert.Equal("The state field 'state' is duplicated.", exception.Message);
    }

    [Fact]
    public void ParseRejectsMalformedJsonAndPreservesCause()
    {
        var exception = Assert.Throws<InvalidDataException>(() => ParseState("{"));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"state\"")]
    public void ParseRejectsNonObjectJson(string payload)
    {
        Assert.Throws<InvalidDataException>(() => ParseState(payload));
    }

    [Fact]
    public void ParseRejectsNullPayload()
    {
        Assert.Throws<InvalidDataException>(() => StateResponse.Parse(null!, ReceiptTime));
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

        Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));
    }

    [Theory]
    [InlineData("timestamp", "\"1234\"")]
    [InlineData("state", "0")]
    [InlineData("speed", "\"30\"")]
    [InlineData("position", "\"12.5\"")]
    [InlineData("sessionId", "0")]
    public void ParseRejectsWrongJsonValueKind(string field, string value)
    {
        var payload = ValidPayload();
        payload[field] = JsonNode.Parse(value);

        Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));
    }

    [Fact]
    public void ParseRejectsUnsupportedState()
    {
        var payload = ValidPayload();
        payload["state"] = "strokeEngine.unknown";

        Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));
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

        var exception = Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));

        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public void ParseRejectsMalformedSessionAndPreservesCause()
    {
        var payload = ValidPayload();
        payload["sessionId"] = "not-a-guid";

        var exception = Assert.Throws<InvalidDataException>(() => ParseState(payload.ToJsonString()));

        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void ParseRejectsNonUtcReceipt()
    {
        Assert.Throws<InvalidDataException>(() =>
            StateResponse.Parse(ValidPayload().ToJsonString(), DateTime.UnixEpoch.ToLocalTime()));
    }

    [Fact]
    public void ParseRejectsNonUtcReceiptBeforeMalformedPayload()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            StateResponse.Parse("{", DateTime.UnixEpoch.ToLocalTime()));

        Assert.Equal("The state receipt timestamp must be UTC.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void MapReturnsDeterministicSetCommandsIncludingZeroSpeed()
    {
        var settings = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle,
            0,
            25,
            50,
            50,
            0,
            MotionPattern.SimpleStroke,
            12.5);

        var requests = Request.FromValidatedMotion(Mapping.ValidateAndMap(settings));
        var payloads = requests.Select(static request => request.ToPayload());

        Assert.Equal(
            [
                "set:speed:0",
                "set:stroke:25",
                "set:depth:50",
                "set:sensation:50",
                "set:buffer:0",
                "set:pattern:0",
            ],
            payloads);
        Assert.DoesNotContain(payloads, payload => payload.StartsWith("go:", StringComparison.Ordinal));
        Assert.DoesNotContain(payloads, payload => payload.Contains("position", StringComparison.Ordinal));
        Assert.DoesNotContain(payloads, payload => payload.Contains("state", StringComparison.Ordinal));
    }

    [Fact]
    public void MapNonzeroSpeedAfterOtherSettings()
    {
        var settings = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle,
            25,
            25,
            50,
            50,
            0,
            MotionPattern.SimpleStroke,
            0);

        var payloads = Request.FromValidatedMotion(Mapping.ValidateAndMap(settings)).Select(request => request.ToPayload());

        Assert.Equal(
            [
                "set:stroke:25",
                "set:depth:50",
                "set:sensation:50",
                "set:buffer:0",
                "set:pattern:0",
                "set:speed:25",
            ],
            payloads);
    }

    [Theory]
    [InlineData(null, "set:stroke:25")]
    [InlineData(50, "set:stroke:25")]
    [InlineData(20, "set:depth:50")]
    public void MapWritesDepthFirstOnlyWhenTheNewStrokeExceedsTheCurrentDepth(int? currentDepth, string first)
    {
        var settings = new MotionSettings(
            MotionDeviceState.StrokeEngineIdle,
            25,
            25,
            50,
            50,
            0,
            MotionPattern.SimpleStroke,
            0);

        var payloads = Request.FromValidatedMotion(Mapping.ValidateAndMap(settings), currentDepth)
            .Select(request => request.ToPayload())
            .ToArray();

        Assert.Equal(first, payloads[0]);
        Assert.Equal(6, payloads.Length);
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

        var exception = Assert.Throws<InvalidDataException>(() => Mapping.ValidateAndMap(settings));

        Assert.IsType<ValidationException>(exception.InnerException);
    }

    [Fact]
    public void MapRejectsNullMotionSettings()
    {
        Assert.Throws<ArgumentNullException>(() => Mapping.ValidateAndMap(null!));
    }

    [Fact]
    public void ParseAcknowledgementReturnsEchoedCommand()
    {
        Assert.True(Acknowledgement.TryParse(
            "ok:set:speed:0",
            ZeroSpeedRequest(),
            out var acknowledgement));
        Assert.Equal(new Acknowledgement("set:speed:0"), acknowledgement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok:")]
    [InlineData("ok")]
    [InlineData("fail:set:speed:0")]
    [InlineData("set:speed:0")]
    [InlineData("arbitrary text")]
    public void ParseAcknowledgementRejectsMalformedResponse(string? payload)
    {
        Assert.False(Acknowledgement.TryParse(payload, ZeroSpeedRequest(), out var acknowledgement));
        Assert.Null(acknowledgement);
    }

    [Theory]
    [InlineData("ok:xset:speed:0")]
    [InlineData("ok:set:speed:0x")]
    [InlineData("ok:set:Speed:0")]
    [InlineData("ok:set:speed:0 ")]
    public void ParseAcknowledgementRejectsUnexpectedCommand(string payload)
    {
        Assert.False(Acknowledgement.TryParse(payload, ZeroSpeedRequest(), out var acknowledgement));
        Assert.Null(acknowledgement);
    }

    [Fact]
    public void AcknowledgementRejectsEmptyCommand()
    {
        Assert.Throws<ArgumentException>(() => new Acknowledgement(string.Empty));
    }

    [Fact]
    public void AcknowledgementRejectsNullCommand()
    {
        Assert.Throws<ArgumentNullException>(() => new Acknowledgement(null!));
    }

    [Fact]
    public void ParseAcknowledgementRejectsNullExpectedRequest()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Acknowledgement.TryParse("ok:set:speed:0", null!, out _));
    }

    [Theory]
    [InlineData(null, null, 60, 30, true)]
    [InlineData(null, null, 60, 61, false)]
    [InlineData(null, null, 60, -1, false)]
    [InlineData(0, 30, 40, 20, true)]
    [InlineData(60, null, 40, 20, true)]
    [InlineData(60, 60, 40, 40, true)]
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
            StockOssmDevice.IsCurrentStateCongruent(previousRequested, previousReported, requested, reported));

    [Fact]
    public void ProtocolTypesRemainInternal()
    {
        Assert.False(typeof(Request).IsPublic);
        Assert.False(typeof(Acknowledgement).IsPublic);
        Assert.False(typeof(StateResponse).IsPublic);
        Assert.False(typeof(PairingIdentity).IsPublic);
        Assert.False(typeof(PatternMetadata).IsPublic);
    }

    [Fact]
    public void StockProfileUsesPinnedFirmwareUuids()
    {
        Assert.Equal(new Guid("522b443a-4f53-534d-0001-420badbabe69"), StockFirmwareProfile.Service);
        Assert.Equal(new Guid("522b443a-4f53-534d-0010-420badbabe69"), StockFirmwareProfile.Identity);
        Assert.Equal(new Guid("522b443a-4f53-534d-1000-420badbabe69"), StockFirmwareProfile.Command);
        Assert.Equal(new Guid("522b443a-4f53-534d-1010-420badbabe69"), StockFirmwareProfile.KnobConfiguration);
        Assert.Equal(new Guid("522b443a-4f53-534d-2000-420badbabe69"), StockFirmwareProfile.State);
        Assert.Equal(new Guid("522b443a-4f53-534d-3000-420badbabe69"), StockFirmwareProfile.Patterns);
        Assert.Equal(new Guid("522b443a-4f53-534d-3010-420badbabe69"), StockFirmwareProfile.PatternData);
    }

    [Fact]
    public void PairingIdentityParsesAndNormalizesPinnedFirmwarePayload()
    {
        var identity = PairingIdentity.Parse("a4:cf:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34"u8);

        Assert.Equal("A4:CF:12:34:56:78", identity.WifiMac);
        Assert.Equal("ESP32-S3", identity.Chip);
        Assert.True(identity.WifiConnected);
        Assert.Equal("0123456789abcdef0123456789abcdef", identity.SketchMd5);
        Assert.Equal("1.0.34", identity.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A4:CF:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef")]
    [InlineData("A4:CF:12:34:56:78;ESP32-S3;2;0123456789abcdef0123456789abcdef;1.0.34")]
    [InlineData("not-a-mac;ESP32-S3;1;0123456789abcdef0123456789abcdef;1.0.34")]
    [InlineData("A4:CF:12:34:56:78;;1;0123456789abcdef0123456789abcdef;1.0.34")]
    [InlineData("A4:CF:12:34:56:78;ESP32-S3;1;not-an-md5;1.0.34")]
    [InlineData("A4:CF:12:34:56:78;ESP32-S3;1;0123456789abcdef0123456789abcdef;")]
    public void PairingIdentityRejectsMalformedPayload(string payload) =>
        Assert.Throws<InvalidDataException>(() => PairingIdentity.Parse(System.Text.Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void PatternMetadataParsesPinnedFirmwareJson()
    {
        var patterns = PatternMetadata.Parse("""[{"name":"Simple Stroke","idx":0},{"name":"Teasing Pounding","idx":1},{"name":"Robo Stroke","idx":2},{"name":"Half'n'Half","idx":3},{"name":"Deeper","idx":4},{"name":"Stop'n'Go","idx":5},{"name":"Insist","idx":6}]"""u8);

        Assert.Equal<PatternMetadata>(
            [
                new("Simple Stroke", 0),
                new("Teasing Pounding", 1),
                new("Robo Stroke", 2),
                new("Half'n'Half", 3),
                new("Deeper", 4),
                new("Stop'n'Go", 5),
                new("Insist", 6),
            ],
            patterns);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\"Simple Stroke\",\"idx\":-1}]")]
    [InlineData("[{\"name\":\"\",\"idx\":0}]")]
    [InlineData("[{\"name\":\"Simple Stroke\",\"idx\":0,\"extra\":true}]")]
    [InlineData("[{\"name\":\"Simple Stroke\",\"idx\":0},{\"name\":\"Other\",\"idx\":0}]")]
    public void PatternMetadataRejectsMalformedPayload(string payload) =>
        Assert.Throws<InvalidDataException>(() => PatternMetadata.Parse(System.Text.Encoding.UTF8.GetBytes(payload)));

    private static DeviceState ParseState(string payload) =>
        StateResponse.Parse(payload, ReceiptTime).ToDeviceState();

    private static Request ZeroSpeedRequest() => Request.ZeroSpeed();

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
