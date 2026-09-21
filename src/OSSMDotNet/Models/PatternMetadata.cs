using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSSMDotNet.Models;

internal sealed record PatternMetadata(string Name, int Index)
{
    private const int MaximumPatterns = 64;
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 4,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static IReadOnlyList<PatternMetadata> Parse(ReadOnlySpan<byte> payload)
    {
        PatternPayload[] patterns;
        try
        {
            patterns = JsonSerializer.Deserialize<PatternPayload[]>(payload, Json)
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

    private sealed record PatternPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("idx")] int Index);
}
