namespace OSSMDotNet.Models;

/// <summary>Instructs a live hub subscriber to establish a fresh subscription after bounded delivery failed.</summary>
/// <summary>
/// Signals that a client must replace its local state with a fresh snapshot.
/// </summary>
/// <param name="Code">The reason the subscription must be refreshed.</param>
public sealed record ControlResyncRequired(string Code);
