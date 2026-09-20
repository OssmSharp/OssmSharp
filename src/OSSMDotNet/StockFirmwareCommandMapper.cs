using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OSSMDotNet;

internal static class StockFirmwareCommandMapper
{
    public static IReadOnlyList<string> Map(MotionSettings settings)
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
            Command("speed", settings.Speed),
            Command("stroke", settings.Stroke),
            Command("sensation", settings.Sensation),
            Command("depth", settings.Depth),
            Command("buffer", settings.Buffer),
            Command("pattern", (int)settings.Pattern),
        ];
    }

    private static string Command(string name, int value) =>
        $"set:{name}:{value.ToString(CultureInfo.InvariantCulture)}";
}