using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace OSSMDotNet.ControlService;

/// <summary>Validates callback payloads against the exact JSON SignalR invocation frame budget.</summary>
internal static class ControlHubPayload
{
    internal const int MaximumBytes = 16_384;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static void EnsureFits(string method, object payload)
    {
        var bytes = SerializeInvocation(method, payload);
        try
        {
            if (bytes.Length > MaximumBytes)
                throw new HubException("outbound-payload-too-large");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static int InvocationByteCount(string method, object payload)
    {
        var bytes = SerializeInvocation(method, payload);
        try { return bytes.Length; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] SerializeInvocation(string method, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new InvocationEnvelope(1, method, [payload]),
            SerializerOptions);
        var framed = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, framed, 0, json.Length);
        framed[^1] = 0x1e; // SignalR JSON protocol record separator.
        CryptographicOperations.ZeroMemory(json);
        return framed;
    }

    private sealed record InvocationEnvelope(int Type, string Target, object[] Arguments);
}
