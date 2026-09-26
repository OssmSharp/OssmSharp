using Microsoft.AspNetCore.WebUtilities;

namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Verifies canonical capability token hashing and rejection of malformed tokens.
/// </summary>
public sealed class CapabilityTokensTests
{
    private const string Token = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [Fact]
    public void CanonicalThirtyTwoByteTokenUsesTheDocumentedSha256DigestAndMatchesOnlyItself()
    {
        var expectedHash = Convert.FromHexString("ea866a757e4c38babfa8127cbe9a409d3e1f93a00ff1488ff735fcf917afffd0");
        var token = WebEncoders.Base64UrlEncode(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var hash = CapabilityTokens.Hash(token);

        Assert.Equal(Token, token);
        Assert.Equal(expectedHash, hash);
        Assert.True(CapabilityTokens.Matches(hash, token));
        Assert.False(CapabilityTokens.Matches(hash, Token[..^1] + "9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHg")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh+")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh/")]
    public void EmptyMalformedOrWrongTokenNeverMatchesTheConfiguredDigest(string? token)
    {
        var hash = CapabilityTokens.Hash(Token);

        Assert.False(CapabilityTokens.Matches(hash, token));
    }
}
