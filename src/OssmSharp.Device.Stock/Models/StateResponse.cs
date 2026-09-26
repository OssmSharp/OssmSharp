using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace OssmSharp.Device.Stock.Models;

/// <summary>
/// Holds validated stock-firmware state with the UTC time at which it was received.
/// </summary>
internal sealed record StateResponse
{
    private StateResponse(
        uint firmwareTimestamp,
        DateTime receivedAtUtc,
        MotionSettings motionSettings,
        Guid sessionId)
    {
        FirmwareTimestamp = firmwareTimestamp;
        ReceivedAtUtc = receivedAtUtc;
        MotionSettings = motionSettings;
        SessionId = sessionId;
    }

    /// <summary>
    /// Gets the device uptime timestamp, not a wall-clock time.
    /// </summary>
    public uint FirmwareTimestamp { get; }

    /// <summary>
    /// Gets the UTC time at which this state was received.
    /// </summary>
    public DateTime ReceivedAtUtc { get; }

    /// <summary>
    /// Gets the motion settings reported by the device.
    /// </summary>
    public MotionSettings MotionSettings { get; }

    /// <summary>
    /// Gets the reported session identifier, if present.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Parses stock-firmware state without treating its uptime timestamp as a wall clock.
    /// </summary>
    /// <param name="payload">The JSON state reported by the firmware.</param>
    /// <param name="receivedAtUtc">The UTC time at which the state was received.</param>
    /// <returns>Validated state and receipt metadata.</returns>
    /// <exception cref="InvalidDataException">The receipt time is not UTC or the state is malformed.</exception>
    public static StateResponse Parse(string payload, DateTime receivedAtUtc)
    {
        if (receivedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidDataException("The state receipt timestamp must be UTC.");
        }

        if (payload is null)
        {
            throw new InvalidDataException("The state payload is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The state payload must be a JSON object.");
            }

            ValidateProperties(root);
            var firmwareTimestamp = ReadFirmwareTimestamp(root);
            var settings = new MotionSettings(
                MapState(ReadString(root, "state")),
                ReadPercentage(root, "speed"),
                ReadPercentage(root, "stroke"),
                ReadPercentage(root, "sensation"),
                ReadPercentage(root, "depth"),
                ReadPercentage(root, "buffer"),
                MapPattern(ReadInteger(root, "pattern")),
                ReadFiniteNumber(root, "position"));
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);

            var session = ReadString(root, "sessionId");
            return new StateResponse(
                firmwareTimestamp,
                receivedAtUtc,
                settings,
                session.Length == 0 ? Guid.Empty : Guid.Parse(session));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ValidationException)
        {
            throw new InvalidDataException("The state payload is invalid.", exception);
        }
    }

    /// <summary>
    /// Projects validated firmware state into a device state with its receipt time.
    /// </summary>
    /// <returns>The observed device state without an inferred velocity.</returns>
    public DeviceState ToDeviceState() =>
        new()
        {
            Timestamp = ReceivedAtUtc,
            MotionSettings = MotionSettings,
            SessionId = SessionId,
            VelocityMillimetresPerSecond = null,
        };

    private static void ValidateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException($"The state field '{property.Name}' is duplicated.");
            }

            if (property.Name is not (
                "timestamp" or
                "state" or
                "speed" or
                "stroke" or
                "sensation" or
                "depth" or
                "buffer" or
                "pattern" or
                "position" or
                "sessionId"))
            {
                throw new InvalidDataException($"The state field '{property.Name}' is unsupported.");
            }
        }
    }

    private static uint ReadFirmwareTimestamp(JsonElement root)
    {
        var value = ReadProperty(root, "timestamp");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var timestamp))
        {
            throw new InvalidDataException("The state field 'timestamp' must be an unsigned 32-bit integer.");
        }

        return timestamp;
    }

    private static string ReadString(JsonElement root, string name)
    {
        var value = ReadProperty(root, name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
        {
            throw new InvalidDataException($"The state field '{name}' must be a string.");
        }

        return result;
    }

    private static int ReadInteger(JsonElement root, string name)
    {
        var value = ReadProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"The state field '{name}' must be an integer.");
        }

        return result;
    }

    private static int ReadPercentage(JsonElement root, string name)
    {
        var value = ReadInteger(root, name);
        if (value is < 0 or > 100)
        {
            throw new InvalidDataException($"The state field '{name}' must be between 0 and 100.");
        }

        return value;
    }

    private static double ReadFiniteNumber(JsonElement root, string name)
    {
        var value = ReadProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException($"The state field '{name}' must be a finite number.");
        }

        return result;
    }

    private static JsonElement ReadProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new InvalidDataException($"The state field '{name}' is required.");
        }

        return value;
    }

    private static MotionDeviceState MapState(string state) => state switch
    {
        "idle" => MotionDeviceState.Idle,
        "homing" => MotionDeviceState.Homing,
        "homing.forward" => MotionDeviceState.HomingForward,
        "homing.backward" => MotionDeviceState.HomingBackward,
        "menu" => MotionDeviceState.Menu,
        "menu.idle" => MotionDeviceState.MenuIdle,
        "simplePenetration" => MotionDeviceState.SimplePenetration,
        "simplePenetration.preflight" => MotionDeviceState.SimplePenetrationPreflight,
        "simplePenetration.idle" => MotionDeviceState.SimplePenetrationIdle,
        "strokeEngine" => MotionDeviceState.StrokeEngine,
        "strokeEngine.preflight" => MotionDeviceState.StrokeEnginePreflight,
        "strokeEngine.idle" => MotionDeviceState.StrokeEngineIdle,
        "strokeEngine.pattern" => MotionDeviceState.StrokeEnginePattern,
        "streaming" => MotionDeviceState.Streaming,
        "streaming.preflight" => MotionDeviceState.StreamingPreflight,
        "streaming.idle" => MotionDeviceState.StreamingIdle,
        "pairing" => MotionDeviceState.Pairing,
        "pairing.idle" => MotionDeviceState.PairingIdle,
        "pairing.success" => MotionDeviceState.PairingSuccess,
        "pairing.success.idle" => MotionDeviceState.PairingSuccessIdle,
        "pairing.wifi" => MotionDeviceState.PairingWifi,
        "pairing.wifi.idle" => MotionDeviceState.PairingWifiIdle,
        "update" => MotionDeviceState.Update,
        "update.checking" => MotionDeviceState.UpdateChecking,
        "update.idle" => MotionDeviceState.UpdateIdle,
        "wifi" => MotionDeviceState.Wifi,
        "wifi.idle" => MotionDeviceState.WifiIdle,
        "help" => MotionDeviceState.Help,
        "help.idle" => MotionDeviceState.HelpIdle,
        "error" => MotionDeviceState.Error,
        "error.idle" => MotionDeviceState.ErrorIdle,
        "error.help" => MotionDeviceState.ErrorHelp,
        "restart" => MotionDeviceState.Restart,
        _ => throw new InvalidDataException($"The state value '{state}' is unsupported."),
    };

    private static MotionPattern MapPattern(int pattern) => pattern switch
    {
        0 => MotionPattern.SimpleStroke,
        1 => MotionPattern.TeasingPounding,
        2 => MotionPattern.RoboStroke,
        3 => MotionPattern.HalfAndHalf,
        4 => MotionPattern.Deeper,
        5 => MotionPattern.StopAndGo,
        6 => MotionPattern.Insist,
        _ => throw new InvalidDataException($"The pattern value '{pattern}' is unsupported."),
    };
}
