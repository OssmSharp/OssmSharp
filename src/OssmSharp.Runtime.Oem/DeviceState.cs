using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Contains complete last-observed firmware controls, without compact protocol metadata or expiry.
/// </summary>
public sealed record DeviceState
{
    /// <summary>
    /// Initializes a state representing the quantized firmware result of requested settings.
    /// </summary>
    /// <param name="settings">The requested controls; firmware velocity is rounded down to floor(Speed * Stroke / 100).</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public DeviceState(MotionSettings settings)
        : this((settings ?? throw new ArgumentNullException(nameof(settings))).Pattern, settings.DesiredDeviceVelocityPercent, settings.Stroke, settings.Depth, settings.Sensation)
    {
    }

    /// <summary>
    /// Initializes a state from raw firmware controls in one device snapshot.
    /// </summary>
    /// <param name="pattern">The observed pattern.</param>
    /// <param name="currentDeviceVelocityPercent">The finite raw velocity from zero through 100 percent, including decimals; this is not speed.</param>
    /// <param name="stroke">The observed whole-number stroke percentage.</param>
    /// <param name="depth">The observed whole-number depth percentage.</param>
    /// <param name="sensation">The observed whole-number sensation percentage.</param>
    /// <remarks>
    /// Percentages use 50 for 50%, not 0.5. Speed is 100 * currentDeviceVelocityPercent / stroke,
    /// without rounding or limiting it to 100%; zero stroke gives null. Depth and sensation do not affect
    /// the conversion. These percentages do not measure physical speed or strokes per minute.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A control is undefined, outside its range, or firmware velocity is not finite.</exception>
    public DeviceState(StrokePattern pattern, double currentDeviceVelocityPercent, int stroke, int depth, int sensation)
    {
        var settings = new MotionSettings(pattern, 0, stroke, depth, sensation);
        if (!double.IsFinite(currentDeviceVelocityPercent) || currentDeviceVelocityPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDeviceVelocityPercent), currentDeviceVelocityPercent, "Firmware velocity must be finite and between zero and 100.");
        }

        Pattern = settings.Pattern;
        CurrentDeviceVelocityPercent = currentDeviceVelocityPercent;
        Stroke = settings.Stroke;
        Depth = settings.Depth;
        Sensation = settings.Sensation;
    }

    /// <summary>
    /// Gets the observed firmware pattern.
    /// </summary>
    public StrokePattern Pattern { get; }

    /// <summary>
    /// Gets the current speed, or null when the observed stroke is zero.
    /// </summary>
    /// <remarks>
    /// Calculated as 100 * firmware velocity / stroke from the same snapshot. Fractional values and values
    /// above 100% are preserved without rounding or capping. This is not physical speed or strokes per minute.
    /// </remarks>
    public double? Speed => Stroke == 0 ? null : 100 * CurrentDeviceVelocityPercent / Stroke;

    /// <summary>
    /// Gets the observed whole-number stroke percentage.
    /// </summary>
    public int Stroke { get; }

    /// <summary>
    /// Gets the observed whole-number depth percentage.
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Gets the observed whole-number sensation percentage.
    /// </summary>
    public int Sensation { get; }

    internal double CurrentDeviceVelocityPercent { get; }

    /// <summary>
    /// Creates a device state from a JSON payload.
    /// </summary>
    /// <param name="data">A serialized JSON object containing pattern, raw firmware speed, stroke, depth, and sensation values; speed may be fractional.</param>
    /// <returns>The device state represented by <paramref name="data"/>.</returns>
    /// <exception cref="InvalidDataException">The payload is null, empty, larger than 512 bytes, or does not contain valid control values.</exception>
    public static DeviceState FromBytes(byte[]? data) => FromBytes(data.AsMemory());

    /// <summary>
    /// Parses observed firmware controls, collecting independently detectable validation errors.
    /// </summary>
    /// <param name="data">A JSON object of at most 512 bytes containing a defined integer pattern, finite raw firmware speed from zero through 100, and integer stroke, depth, and sensation percentages.</param>
    /// <param name="state">The parsed state on success; otherwise, null.</param>
    /// <param name="errors">An empty list on success; otherwise, errors with JSON field names in <see cref="ValidationResult.MemberNames"/>, or "$" for payload-level errors.</param>
    /// <returns>True if the payload contains valid control values; otherwise, false.</returns>
    /// <remarks>
    /// Duplicate fields are rejected. Unknown fields are otherwise ignored. Malformed JSON, excessive nesting,
    /// invalid payload size, or a non-object root produces a payload-level error without field validation.
    /// The speed field is raw velocity, not speed. Its decimals are preserved when
    /// calculating <see cref="Speed"/> from the stroke in the same payload; zero stroke gives null.
    /// </remarks>
    public static bool TryFromBytes(byte[]? data, [NotNullWhen(true)] out DeviceState? state, out IReadOnlyList<ValidationResult> errors)
        => TryFromBytes(data.AsMemory(), out state, out errors);

    internal static DeviceState FromBytes(ReadOnlyMemory<byte> data)
    {
        if (TryFromBytes(data, out var state, out var errors))
        {
            return state;
        }

        throw new InvalidDataException(string.Join(" ", errors.Select(error => error.ErrorMessage)));
    }

    private static bool TryFromBytes(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out DeviceState? state, out IReadOnlyList<ValidationResult> errors)
    {
        state = null;
        var results = new List<ValidationResult>();
        errors = results;
        if (data.IsEmpty || data.Length > 512)
        {
            results.Add(new("Payload must contain between one and 512 bytes.", ["$"]));
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                results.Add(new("Payload must be a JSON object.", ["$"]));
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var controls = new Dictionary<string, int>(StringComparer.Ordinal);
            double currentDeviceVelocityPercent = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    results.Add(new($"Field '{property.Name}' must not be duplicated.", [property.Name]));
                }

                if (property.Name == "pattern")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var pattern))
                    {
                        results.Add(new("Field 'pattern' must be a 32-bit integer.", [property.Name]));
                    }
                    else if (!MotionSettings.IsValidPattern((StrokePattern)pattern))
                    {
                        results.Add(new("Pattern must be a defined stroke pattern.", [property.Name]));
                    }
                    else
                    {
                        controls[property.Name] = pattern;
                    }
                }
                else if (property.Name == "speed")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out currentDeviceVelocityPercent)
                        || !double.IsFinite(currentDeviceVelocityPercent) || currentDeviceVelocityPercent is < 0 or > 100)
                    {
                        results.Add(new("Field 'speed' must be finite and between zero and 100.", ["speed"]));
                    }
                }
                else if (property.Name is "stroke" or "depth" or "sensation")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var percentage))
                    {
                        results.Add(new($"Field '{property.Name}' must be a 32-bit integer.", [property.Name]));
                    }
                    else if (!MotionSettings.IsValidPercentage(percentage))
                    {
                        results.Add(new($"Field '{property.Name}' must be between zero and 100.", [property.Name]));
                    }
                    else
                    {
                        controls[property.Name] = percentage;
                    }
                }
            }

            foreach (var name in new[] { "pattern", "speed", "stroke", "depth", "sensation" })
            {
                if (!names.Contains(name))
                {
                    results.Add(new($"Field '{name}' is required.", [name]));
                }
            }

            if (results.Count != 0)
            {
                return false;
            }

            state = new((StrokePattern)controls["pattern"], currentDeviceVelocityPercent, controls["stroke"], controls["depth"], controls["sensation"]);
            return true;
        }
        catch (JsonException)
        {
            results.Add(new("Payload must contain valid JSON with a maximum depth of eight.", ["$"]));
            return false;
        }
    }
}
