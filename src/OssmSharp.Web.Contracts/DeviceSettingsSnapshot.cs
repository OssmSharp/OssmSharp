namespace OssmSharp.Web.Contracts;

/// <summary>
/// Describes the last complete requested selection successfully sent by the owning session, not observed motion.
/// </summary>
/// <param name="Pattern">The requested pattern, if available.</param>
/// <param name="Speed">The requested speed (as an integer percentage) in [0, 100], not rounded raw velocity; null if no successful selection is available.</param>
/// <param name="Stroke">The requested stroke percentage, if available.</param>
/// <param name="Depth">The requested depth percentage, if available.</param>
/// <param name="Sensation">The requested sensation percentage, if available.</param>
public sealed record DeviceSettingsSnapshot(int? Pattern, int? Speed, int? Stroke, int? Depth, int? Sensation);
