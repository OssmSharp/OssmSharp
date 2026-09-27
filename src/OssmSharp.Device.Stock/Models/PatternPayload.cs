using System.Text.Json.Serialization;

namespace OssmSharp.Device.Stock.Models;

internal sealed record PatternPayload()
{
    [property: JsonPropertyName("name")]
    public required string Name { get; set; }

    [property: JsonPropertyName("idx")]
    public required int Index { get; set; }
}
