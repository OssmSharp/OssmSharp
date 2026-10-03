namespace OssmSharp.Web.Contracts;

/// <summary>
/// Contains one complete settings request from a UI.
/// </summary>
/// <param name="Correlation">The caller-provided request identifier.</param>
/// <param name="Pattern">The runtime pattern value.</param>
/// <param name="Speed">The requested speed (as an integer percentage) in [0, 100], using 50 for 50%.</param>
/// <param name="Stroke">The requested stroke percentage in [0, 100].</param>
/// <param name="Depth">The requested depth percentage.</param>
/// <param name="Sensation">The requested sensation percentage.</param>
/// <remarks>
/// The runtime alone converts speed to firmware velocity, rounding down to a whole percentage.
/// Zero speed or stroke, or a converted velocity below 1%, sends zero velocity.
/// Depth and sensation do not affect conversion. Speed is not physical speed or strokes per minute.
/// </remarks>
public sealed record SettingsRequest(
    string Correlation,
    int Pattern,
    int Speed,
    int Stroke,
    int Depth,
    int Sensation);
