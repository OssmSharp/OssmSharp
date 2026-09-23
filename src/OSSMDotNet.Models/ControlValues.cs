namespace OSSMDotNet.Models;

/// <summary>Represents control values carried through the control-service contract.</summary>
/// <summary>
/// Describes guest-visible motion settings without device-only fields.
/// </summary>
/// <param name="Pattern">The selected motion pattern.</param>
/// <param name="Speed">The requested speed.</param>
/// <param name="Stroke">The requested stroke length.</param>
/// <param name="Depth">The requested depth.</param>
/// <param name="Sensation">The requested sensation setting.</param>
public sealed record ControlValues(int Pattern, int Speed, int Stroke, int Depth, int Sensation);
