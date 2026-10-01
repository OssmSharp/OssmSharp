namespace OssmSharp.Web.Contracts;

/// <summary>
/// Contains one complete settings submission from a UI.
/// </summary>
/// <param name="Correlation">The caller-provided request identifier.</param>
/// <param name="Pattern">The runtime pattern value.</param>
/// <param name="Speed">The requested firmware velocity percentage, not a stroke-relative cadence percentage.</param>
/// <param name="Stroke">The requested stroke percentage.</param>
/// <param name="Depth">The requested depth percentage.</param>
/// <param name="Sensation">The requested sensation percentage.</param>
public sealed record SettingsRequest(
    string Correlation,
    int Pattern,
    int Speed,
    int Stroke,
    int Depth,
    int Sensation);
