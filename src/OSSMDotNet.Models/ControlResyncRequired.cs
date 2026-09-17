namespace OSSMDotNet.Models;

/// <summary>Instructs a live hub subscriber to establish a fresh subscription after bounded delivery failed.</summary>
public sealed record ControlResyncRequired(string Code);
