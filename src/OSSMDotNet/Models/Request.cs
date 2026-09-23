using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OSSMDotNet.Models;

/// <summary>
/// Represents one stock-firmware setting command derived from validated motion settings.
/// </summary>
internal sealed record Request
{
    private Request(string setting, int value)
    {
        Setting = setting;
        Value = value;
    }

    /// <summary>
    /// Gets the stock-firmware setting name.
    /// </summary>
    public string Setting { get; }

    /// <summary>
    /// Gets the setting value sent to the firmware.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// Converts motion settings into the ordered stock-firmware setting commands.
    /// </summary>
    /// <param name="settings">The motion settings to validate and send.</param>
    /// <returns>Commands for speed, stroke, sensation, depth, buffer, and pattern, in that order.</returns>
    /// <exception cref="InvalidDataException">The motion settings fail validation.</exception>
    public static IReadOnlyList<Request> FromMotionSettings(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        }
        catch (ValidationException exception)
        {
            throw new InvalidDataException("The motion settings are invalid.", exception);
        }

        return
        [
            new("speed", settings.Speed),
            new("stroke", settings.Stroke),
            new("sensation", settings.Sensation),
            new("depth", settings.Depth),
            new("buffer", settings.Buffer),
            new("pattern", (int)settings.Pattern),
        ];
    }

    /// <summary>
    /// Formats this setting as a stock-firmware command payload.
    /// </summary>
    /// <returns>The command text using invariant numeric formatting.</returns>
    public string ToPayload() =>
        $"set:{Setting}:{Value.ToString(CultureInfo.InvariantCulture)}";
}
