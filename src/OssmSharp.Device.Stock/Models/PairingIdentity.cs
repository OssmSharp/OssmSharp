using System.Globalization;
using System.Text;

namespace OssmSharp.Device.Stock.Models;

/// <summary>
/// Describes the identity fields reported by the stock pairing characteristic.
/// </summary>
/// <param name="WifiMac">The normalized Wi-Fi MAC address.</param>
/// <param name="Chip">The reported chip identifier.</param>
/// <param name="WifiConnected">Whether the device reports a Wi-Fi connection.</param>
/// <param name="SketchMd5">The reported firmware sketch digest.</param>
/// <param name="Version">The reported firmware version.</param>
internal sealed record PairingIdentity(
    string WifiMac,
    string Chip,
    bool WifiConnected,
    string SketchMd5,
    string Version)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Parses the semicolon-separated identity fields from the stock pairing characteristic.
    /// </summary>
    /// <param name="payload">The UTF-8 characteristic value.</param>
    /// <returns>The reported identity with a normalized Wi-Fi MAC address.</returns>
    /// <exception cref="InvalidDataException">The identity is malformed or is not valid UTF-8.</exception>
    internal static PairingIdentity Parse(ReadOnlySpan<byte> payload)
    {
        string value;
        try
        {
            value = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The stock identity payload is not valid UTF-8.", exception);
        }

        var fields = value.Split(';');
        if (fields.Length != 5 ||
            !TryNormalizeMac(fields[0], out var wifiMac) ||
            !IsText(fields[1]) ||
            fields[2] is not ("0" or "1") ||
            !IsMd5(fields[3]) ||
            !IsText(fields[4]))
        {
            throw new InvalidDataException("The stock identity payload is malformed.");
        }

        return new PairingIdentity(
            wifiMac,
            fields[1],
            fields[2] == "1",
            fields[3],
            fields[4]);
    }

    private static bool TryNormalizeMac(string value, out string normalized)
    {
        var octets = value.Split(':');
        if (octets.Length != 6 || octets.Any(octet =>
            octet.Length != 2 ||
            !byte.TryParse(octet, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = string.Join(':', octets).ToUpperInvariant();
        return true;
    }

    private static bool IsMd5(string value) =>
        value.Length == 32 && value.All(character => Uri.IsHexDigit(character));

    private static bool IsText(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
