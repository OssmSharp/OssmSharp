namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Holds the latest confirmed raw firmware assignments in the current connection, not observed physical motion.
/// </summary>
/// <param name="Pattern">The confirmed pattern assignment, or null when none is confirmed.</param>
/// <param name="DeviceVelocityPercent">The confirmed raw velocity from zero through 100, or null when none is confirmed; zero after acknowledged Stop. This is not speed.</param>
/// <param name="Stroke">The confirmed stroke assignment, or null when none is confirmed.</param>
/// <param name="Depth">The confirmed depth assignment, or null when none is confirmed.</param>
/// <param name="Sensation">The confirmed sensation assignment, or null when none is confirmed.</param>
internal sealed record DeviceSettings(
    int? Pattern = null,
    int? DeviceVelocityPercent = null,
    int? Stroke = null,
    int? Depth = null,
    int? Sensation = null);
