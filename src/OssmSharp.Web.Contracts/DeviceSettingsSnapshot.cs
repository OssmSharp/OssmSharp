namespace OssmSharp.Web.Contracts;

/// <summary>Describes confirmed assignments, independently of observed or selected settings.</summary>
/// <param name="Pattern">The confirmed pattern, if available.</param>
/// <param name="Speed">The confirmed speed percentage, if available.</param>
/// <param name="Stroke">The confirmed stroke percentage, if available.</param>
/// <param name="Depth">The confirmed depth percentage, if available.</param>
/// <param name="Sensation">The confirmed sensation percentage, if available.</param>
public sealed record DeviceSettingsSnapshot(int? Pattern, int? Speed, int? Stroke, int? Depth, int? Sensation);