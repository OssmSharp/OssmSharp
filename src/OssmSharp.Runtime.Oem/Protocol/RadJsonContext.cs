using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OssmSharp.Runtime.Oem.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ProtocolInfo))]
[JsonSerializable(typeof(RadResponse))]
[JsonSerializable(typeof(LeaseResult))]
[JsonSerializable(typeof(StreamResult))]
internal sealed partial class RadJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Rejects incompatible protocol information before subscribing or acquiring device control.
    /// </summary>

<<<<<<< TODO: Unmerged change from project 'OssmSharp.Runtime.Oem(net10.0-macos27.0)', Before:
    internal static void ValidateProtocolInfo(byte[]? data)
=======
internal static void ValidateProtocolInfo(byte[]? data)
>>>>>>> After
    internal static void ValidateProtocolInfo(byte[]? data)
    {
        try
        {
            if (JsonSerializer.Deserialize(data ?? [], Default.ProtocolInfo) is { Protocol: "rad-ble", Version: 1 })
            {
                return;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException("The device does not report RAD BLE protocol version 1.");
    }

    /// <summary>
    /// Decodes a required response object and translates absent or malformed results into the operation's protocol error.
    /// </summary>

<<<<<<< TODO: Unmerged change from project 'OssmSharp.Runtime.Oem(net10.0-macos27.0)', Before:
    internal static T ReadResult<T>(JsonElement result, JsonTypeInfo<T> type, string error)
=======
internal static T ReadResult<T>(JsonElement result, JsonTypeInfo<T> type, string error)
>>>>>>> After
    internal static T ReadResult<T>(JsonElement result, JsonTypeInfo<T> type, string error)
        where T : class
    {
        try
        {
            if (result.ValueKind == JsonValueKind.Object && result.Deserialize(type) is { } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException(error);
    }

    /// <summary>
    /// Validates response size, version, and stage before correlation with an outstanding request.
    /// </summary>

<<<<<<< TODO: Unmerged change from project 'OssmSharp.Runtime.Oem(net10.0-macos27.0)', Before:
    internal static RadResponse ParseResponse(byte[]? data, int maximumMessageBytes)
=======
internal static RadResponse ParseResponse(byte[]? data, int maximumMessageBytes)
>>>>>>> After
    internal static RadResponse ParseResponse(byte[]? data, int maximumMessageBytes)
    {
        if (data is null || data.Length > maximumMessageBytes)
        {
            throw new InvalidDataException("A RAD response must contain at most 509 bytes.");
        }

        if (JsonSerializer.Deserialize(data, Default.RadResponse) is not { V: 1, Stage: "accepted" or "completed" or "failed" } response)
        {
            throw new InvalidDataException("A RAD response requires v=1, an id, a known stage, and ok.");
        }

        return response;
    }
}

internal sealed record ProtocolInfo(string Protocol, uint Version);

internal sealed record RadResponse(uint V, uint Id, string Stage, bool Ok, string? Code = null, string? Message = null, JsonElement Result = default, string? StateAfter = null);

internal sealed record LeaseResult(uint Lease = 0, uint TtlMs = 0);

internal sealed record StreamResult(uint StreamId, string Surface);
