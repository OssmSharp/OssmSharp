using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace OssmSharp.Web.ServiceDefaults;

/// <summary>
/// Creates, hashes, and verifies opaque capability tokens.
/// </summary>
public static class CapabilityTokens
{
    /// <summary>
    /// Creates a random URL-safe capability token.
    /// </summary>
    /// <returns>A new token backed by 32 cryptographically random bytes.</returns>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Computes the SHA-256 digest used to retain a capability without its plaintext.
    /// </summary>
    /// <param name="token">The capability to hash.</param>
    /// <returns>The 32-byte digest of the token's UTF-8 representation.</returns>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>
    /// Validates token syntax and compares its digest in constant time.
    /// </summary>
    /// <param name="expectedHash">The stored SHA-256 digest to compare against.</param>
    /// <param name="token">The candidate URL-safe token, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when the token has valid syntax and its digest matches.</returns>
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
