using System.Text.Json.Serialization;

namespace OssmSharp.Device.Stock.Models;

[JsonSerializable(typeof(PatternPayload[]))]
internal sealed partial class PatternJsonContext : JsonSerializerContext;
