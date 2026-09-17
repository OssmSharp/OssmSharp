namespace OSSMDotNet.Models;

/// <summary>Identifies a simulator-only fault or state injection operation.</summary>
public sealed record SimulatorFaultRequest(string Kind);
