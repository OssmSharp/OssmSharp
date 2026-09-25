using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace OSSMDotNet.ApiService.SignalR;

/// <summary>
/// Validates callback payloads against the exact JSON SignalR invocation frame budget.
/// </summary>
internal static class ControlHubPayload
{
    /// <summary>
    /// Gets the maximum outbound invocation size in UTF-8 bytes, including the record separator.
    /// </summary>
    internal const int MaximumBytes = 16_384;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Rejects a callback that would exceed the outbound invocation budget.
    /// </summary>
    /// <param name="method">The hub callback method name.</param>
    /// <param name="payload">The callback payload to measure.</param>
    /// <exception cref="HubException">The serialized invocation exceeds <see cref="MaximumBytes"/>.</exception>
    internal static void EnsureFits(string method, object payload)
    {
        if (InvocationByteCount(method, payload) > MaximumBytes)
            throw new HubException("outbound-payload-too-large");
    }

    /// <summary>
    /// Counts the UTF-8 bytes of a SignalR JSON invocation, including its record separator.
    /// </summary>
    /// <param name="method">The hub callback method name.</param>
    /// <param name="payload">The callback payload to serialize.</param>
    /// <returns>The encoded frame size in bytes.</returns>
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
