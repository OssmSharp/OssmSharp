using System.Globalization;
using System.Text;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Device.Oem;

/// <summary>
/// Encodes one firmware control assignment without interpreting patterns or changing values.
/// </summary>
public static class Request
{
    /// <summary>
    /// Encodes a single assignment in the OEM firmware's UTF-8 command format.
    /// </summary>
    /// <param name="command">The validated control assignment.</param>
    /// <returns>The command bytes, with no terminator or additional commands.</returns>
    /// <remarks>
    /// Values are preserved, not rounded or clamped. Firmware owns acceptance and application;
    /// firmware 1.0.34 ignores fractional values and pattern identifiers above 100.
    /// Successful transmission does not establish acceptance or physical motion.
    /// </remarks>
    public static byte[] Encode(DeviceCommand command)
    {
        var control = command.Control switch
        {
            DeviceControl.Pattern => "pattern",
            DeviceControl.Speed => "speed",
            DeviceControl.Stroke => "stroke",
            DeviceControl.Depth => "depth",
            DeviceControl.Sensation => "sensation",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Control, "Unknown firmware control."),
        };

        return Encoding.UTF8.GetBytes($"set:{control}:{command.Value.ToString("R", CultureInfo.InvariantCulture)}");
    }
}
