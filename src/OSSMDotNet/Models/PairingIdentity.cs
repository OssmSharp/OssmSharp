using System.Globalization;
using System.Text;

namespace OSSMDotNet.Models;

internal sealed record PairingIdentity(
    string WifiMac,
    string Chip,
    bool WifiConnected,
    string SketchMd5,
    string Version)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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
