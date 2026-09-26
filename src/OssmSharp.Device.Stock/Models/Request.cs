using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OssmSharp.Device.Stock.Models;

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
    /// <remarks>Changing the motion envelope requires a confirmed stop and a validated intermediate sequence before these commands are dispatched.</remarks>
    /// <param name="settings">The motion settings to validate and send.</param>
    /// <returns>Commands for stroke, sensation, depth, buffer and pattern, with speed first when zero and last otherwise.</returns>
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

        var requests = new List<Request>(6);
        if (settings.Speed == 0)
        {
            requests.Add(new("speed", 0));
        }

        requests.Add(new("stroke", settings.Stroke));
        requests.Add(new("sensation", settings.Sensation));
        requests.Add(new("depth", settings.Depth));
        requests.Add(new("buffer", settings.Buffer));
        requests.Add(new("pattern", (int)settings.Pattern));

        if (settings.Speed != 0)
        {
            requests.Add(new("speed", settings.Speed));
        }

        return requests;
    }

    /// <summary>
    /// Formats this setting as a stock-firmware command payload.
    /// </summary>
    /// <returns>The command text using invariant numeric formatting.</returns>
    public string ToPayload() =>
        $"set:{Setting}:{Value.ToString(CultureInfo.InvariantCulture)}";
}
