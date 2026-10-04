using System.Text.Json;
using System.Text.Json.Serialization;

namespace OssmSharp.Runtime.Oem.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ProtocolInfo))]
[JsonSerializable(typeof(RadResponse))]
[JsonSerializable(typeof(LeaseResult))]
[JsonSerializable(typeof(StreamResult))]
internal sealed partial class RadJsonContext : JsonSerializerContext;

internal sealed record ProtocolInfo(string Protocol, uint Version);

internal sealed record RadResponse(uint V, uint Id, string Stage, bool Ok, string? Code = null, string? Message = null, JsonElement Result = default, string? StateAfter = null);

internal sealed record LeaseResult(uint Lease = 0, uint TtlMs = 0);

internal sealed record StreamResult(uint StreamId, string Surface);
