namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Holds the latest confirmed assignments in the current connection, not observed physical motion.
/// </summary>
/// <param name="Pattern">The confirmed pattern assignment, or null when none is confirmed.</param>
/// <param name="Speed">The confirmed speed assignment, including zero after acknowledged Stop.</param>
/// <param name="Stroke">The confirmed stroke assignment, or null when none is confirmed.</param>
/// <param name="Depth">The confirmed depth assignment, or null when none is confirmed.</param>
/// <param name="Sensation">The confirmed sensation assignment, or null when none is confirmed.</param>
/// <remarks>Unacknowledged assignments may still take effect and are not represented here.</remarks>
public sealed record TransmittedControls(
    int? Pattern = null,
    int? Speed = null,
    int? Stroke = null,
    int? Depth = null,
    int? Sensation = null);