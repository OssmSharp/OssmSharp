using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Serializes RAD BLE v1 requests without interpreting patterns or changing values.
/// </summary>
public static class Request
{
    /// <summary>
    /// Serializes one RAD BLE v1 request as UTF-8 JSON.
    /// </summary>
    /// <param name="id">The nonzero identifier that correlates responses with this request.</param>
    /// <param name="operation">The RAD operation, such as <c>setting.write</c>.</param>
    /// <param name="path">The optional resource path, such as <c>motion.speed</c>.</param>
    /// <param name="arguments">Optional whole-number arguments.</param>
    /// <param name="lease">The optional control lease token.</param>
    /// <returns>The request bytes. The caller enforces the transport's message size limit.</returns>
    /// <remarks>
    /// Values are preserved, not rounded or clamped. Firmware owns acceptance and application;
    /// a successful write or response does not establish physical motion.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is empty.</exception>
    public static byte[] Encode(
        uint id,
        string operation,
        string? path = null,
        IReadOnlyDictionary<string, int>? arguments = null,
        uint? lease = null)
    {
        JsonObject? jsonArguments = null;
        if (arguments is not null)
        {
            jsonArguments = new JsonObject();
            foreach (var (name, value) in arguments)
            {
                jsonArguments.Add(name, JsonValue.Create(value));
            }
        }

        return EncodeWithArguments(id, operation, path, jsonArguments, lease);
    }

    internal static byte[] EncodeWithArguments(uint id, string operation, string? path, JsonObject? arguments, uint? lease)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteNumber("id", id);
            writer.WriteString("op", operation);
            if (path is not null)
            {
                writer.WriteString("path", path);
            }

            if (arguments is not null)
            {
                writer.WritePropertyName("args");
                arguments.WriteTo(writer);
            }

            if (lease is { } token)
            {
                writer.WriteNumber("lease", token);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
