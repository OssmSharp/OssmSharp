using System.Buffers.Binary;
using System.Text.Json;

namespace OSSMDotNet.Models;

/// <summary>Reads and writes length-prefixed, size-bounded guest-pipe frames.</summary>
public static class PipeFrames
{
    public const int MaximumLength = 16_384;

    /// <summary>Reads one valid frame from a stream.</summary>
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumLength)
            throw new InvalidDataException("invalid-frame-size");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    /// <summary>Serializes and writes one valid frame to a stream.</summary>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, GuestProtocol.Json);
        if (bytes.Length is 0 or > MaximumLength)
            throw new InvalidDataException("invalid-frame-size");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
