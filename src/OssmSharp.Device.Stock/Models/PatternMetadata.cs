using System.Text.Json;
using System.Text.Json.Serialization;

namespace OssmSharp.Device.Stock.Models;

/// <summary>
/// Describes a pattern name and index reported by the stock firmware.
/// </summary>
/// <param name="Name">The name displayed for the pattern.</param>
/// <param name="Index">The nonnegative firmware pattern index.</param>
internal sealed record PatternMetadata(string Name, int Index)
{
    private const int MaximumPatterns = 64;
    private static readonly PatternJsonContext Json = new(new JsonSerializerOptions
    {
        AllowDuplicateProperties = false,
        MaxDepth = 4,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    });

    /// <summary>
    /// Parses the firmware's bounded list of supported motion patterns.
    /// </summary>
    /// <param name="payload">The UTF-8 JSON pattern list.</param>
    /// <returns>Between one and 64 patterns with unique nonnegative indices.</returns>
    /// <exception cref="InvalidDataException">The list is malformed or contains invalid or duplicate patterns.</exception>
    internal static IReadOnlyList<PatternMetadata> Parse(ReadOnlySpan<byte> payload)
    {
        PatternPayload[] patterns;
        try
        {
            patterns = JsonSerializer.Deserialize(payload, Json.PatternPayloadArray)
                ?? throw new InvalidDataException("The stock pattern payload is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The stock pattern payload is malformed.", exception);
        }

        if (patterns.Length is 0 or > MaximumPatterns)
        {
            throw new InvalidDataException($"The stock pattern payload must contain 1 through {MaximumPatterns} patterns.");
        }

        var indices = new HashSet<int>();
        var result = new PatternMetadata[patterns.Length];
        for (var index = 0; index < patterns.Length; index++)
        {
            var pattern = patterns[index];
            if (string.IsNullOrWhiteSpace(pattern.Name) ||
                pattern.Name.Any(char.IsControl) ||
                pattern.Index < 0 ||
                !indices.Add(pattern.Index))
            {
                throw new InvalidDataException("The stock pattern payload contains invalid or duplicate metadata.");
            }

            result[index] = new PatternMetadata(pattern.Name, pattern.Index);
        }

        return result;
    }
}
