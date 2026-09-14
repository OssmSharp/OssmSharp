using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace OSSMDotNet.Web.HostRuntime;

public static class CapabilityTokens
{
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool Matches(ReadOnlySpan<byte> expectedHash, string? token)
    {
        if (token is not { Length: >= 32 and <= 128 })
            return false;
        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }
        Span<byte> encoded = stackalloc byte[128];
        Span<byte> digest = stackalloc byte[32];
        var length = Encoding.UTF8.GetBytes(token.AsSpan(), encoded);
        SHA256.HashData(encoded[..length], digest);
        var matches = CryptographicOperations.FixedTimeEquals(expectedHash, digest);
        CryptographicOperations.ZeroMemory(encoded);
        CryptographicOperations.ZeroMemory(digest);
        return matches;
    }

}
