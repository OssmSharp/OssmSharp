using System.Buffers;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSSMDotNet.DeviceCli;

/// <summary>
/// Reads and writes bounded JSON-lines messages for the private simulator host pipe.
/// </summary>
internal static class SimulatorIpcProtocol
{
    private const int MaximumFrameBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Gets the serializer settings shared by host requests and responses.
    /// </summary>
    internal static JsonSerializerOptions SerializerOptions => Json;

    /// <summary>
    /// Reads one newline-delimited JSON value from a pipe.
    /// </summary>
    /// <typeparam name="T">The expected JSON value type.</typeparam>
    /// <param name="pipe">The connected pipe.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>A task that completes with the deserialized value.</returns>
    internal static async Task<T> ReadAsync<T>(PipeStream pipe, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumFrameBytes);
        try
        {
            var length = 0;
            while (length < MaximumFrameBytes)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The simulator host closed the connection before completing its response.");
                }

                if (buffer[length++] == (byte)'\n')
                {
                    return JsonSerializer.Deserialize<T>(Utf8.GetString(buffer, 0, length - 1), Json)
                        ?? throw new InvalidDataException("The simulator host returned an empty message.");
                }
            }

            throw new InvalidDataException($"The simulator host message exceeds {MaximumFrameBytes} bytes.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Writes one bounded newline-delimited JSON value to a pipe.
    /// </summary>
    /// <typeparam name="T">The JSON value type.</typeparam>
    /// <param name="pipe">The connected pipe.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>A task that completes after the message is flushed.</returns>
    internal static async Task WriteAsync<T>(PipeStream pipe, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length + 1 > MaximumFrameBytes)
        {
            throw new InvalidDataException($"The simulator host message exceeds {MaximumFrameBytes} bytes.");
        }

        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
