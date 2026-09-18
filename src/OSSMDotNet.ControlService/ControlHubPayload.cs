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
        if (InvocationByteCount(method, payload) > MaximumBytes)
            throw new HubException("outbound-payload-too-large");
    }

    internal static int InvocationByteCount(string method, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new InvocationEnvelope(1, method, [payload]),
            SerializerOptions);
        try { return checked(json.Length + 1); }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private sealed record InvocationEnvelope(int Type, string Target, object[] Arguments);
}
