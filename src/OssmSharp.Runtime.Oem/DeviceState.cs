using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Contains complete last-observed firmware controls, without compact protocol metadata, expiry, or inference about physical motion or safety.
/// </summary>
public sealed record DeviceState
{
    /// <summary>
    /// Initializes an observation of the device's control arguments.
    /// </summary>
    /// <param name="settings">The observed control arguments, not a requested motion or physical position.</param>
    public DeviceState(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
    }

    /// <summary>
    /// Gets the observed firmware settings.
    /// </summary>
    public MotionSettings Settings { get; }

    /// <summary>
    /// Creates a device state from a JSON payload.
    /// </summary>
    /// <param name="data">A serialized JSON object containing pattern, speed, stroke, depth, and sensation values.</param>
    /// <returns>The device state represented by <paramref name="data"/>.</returns>
    /// <exception cref="InvalidDataException">The payload is null, empty, larger than 512 bytes, or does not contain valid control values.</exception>
    public static DeviceState FromBytes(byte[]? data)
    {
        if (TryFromBytes(data, out var state, out var errors))
        {
            return state;
        }

        throw new InvalidDataException(string.Join(" ", errors.Select(error => error.ErrorMessage)));
    }

    /// <summary>
    /// Parses observed firmware controls, collecting independently detectable validation errors.
    /// </summary>
    /// <param name="data">A JSON object of at most 512 bytes containing a defined integer pattern and numeric speed, stroke, depth, and sensation values.</param>
    /// <param name="state">The parsed state on success; otherwise, null.</param>
    /// <param name="errors">An empty list on success; otherwise, errors with JSON field names in <see cref="ValidationResult.MemberNames"/>, or "$" for payload-level errors.</param>
    /// <returns>True if the payload contains valid control values; otherwise, false.</returns>
    /// <remarks>
    /// Duplicate fields are rejected. Unknown fields are otherwise ignored. Malformed JSON, excessive nesting,
    /// invalid payload size, or a non-object root produces a payload-level error without field validation.
    /// </remarks>
    public static bool TryFromBytes(byte[]? data, [NotNullWhen(true)] out DeviceState? state, out IReadOnlyList<ValidationResult> errors)
    {
        state = null;
        var results = new List<ValidationResult>();
        errors = results;
        if (data is null || data.Length is 0 or > 512)
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
            var controls = new Dictionary<string, double>(StringComparer.Ordinal);
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
                else if (property.Name is "speed" or "stroke" or "depth" or "sensation")
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var percentage))
                    {
                        results.Add(new($"Field '{property.Name}' must be a number.", [property.Name]));
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

            state = new(new MotionSettings((StrokePattern)(int)controls["pattern"], controls["speed"], controls["stroke"], controls["depth"], controls["sensation"]));
            return true;
        }
        catch (JsonException)
        {
            results.Add(new("Payload must contain valid JSON with a maximum depth of eight.", ["$"]));
            return false;
        }
    }
}
