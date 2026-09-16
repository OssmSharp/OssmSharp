using System.Text.Json;
using System.Text.Json.Serialization;
namespace OSSMDotNet.Models;

/// <summary>Defines bounded JSON parsing and validation for restricted guest-pipe calls.</summary>
public static class GuestProtocol
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 12
    };

    /// <summary>Decodes and validates a guest-pipe call from a bounded frame.</summary>
    public static GuestCall DecodeCall(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is 0 or > 4096)
                throw new InvalidDataException("invalid-frame-size");
            var call = JsonSerializer.Deserialize<GuestCall>(bytes, Json) ?? throw new InvalidDataException("invalid-request");
            if (call.V != 1 || !ValidText(call.RequestId, 64) || !ValidText(call.ConnectionId, 64) ||
                !ValidText(call.SessionId, 64) || !ValidText(call.Proof, 128))
                throw new InvalidDataException("invalid-request");
            if (call.Op is not ("join" or "status" or "input" or "stop" or "leave"))
                throw new InvalidDataException("operation-not-allowed");
            if ((call.Op == "input") != (call.Input is not null))
                throw new InvalidDataException("unexpected-payload");
            if (call.Input is { } input)
            {
                if (input.V != 2 || !input.Run || !ValidText(input.Epoch, 64) || !ValidText(input.GrantId, 64) ||
                    input.PolicyVersion < 1 || input.Seq is < 1 or > 9_007_199_254_740_991 || input.Desired is null)
                    throw new InvalidDataException("invalid-input");
            }
            return call;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("invalid-json");
        }
    }

    /// <summary>Returns whether text is nonempty and within the specified protocol bound.</summary>
    public static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
