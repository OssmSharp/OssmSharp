using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OSSMDotNet.Models;

internal sealed record Request
{
    private Request(string setting, int value)
    {
        Setting = setting;
        Value = value;
    }

    public string Setting { get; }

    public int Value { get; }

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

    public string ToPayload() =>
        $"set:{Setting}:{Value.ToString(CultureInfo.InvariantCulture)}";
}
