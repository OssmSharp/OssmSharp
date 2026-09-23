namespace OSSMDotNet.Models;

/// <summary>Identifies a simulator-only fault or state injection operation.</summary>
/// <summary>
/// Requests a named simulator fault for operator diagnostics.
/// </summary>
/// <param name="Kind">The fault or state transition to inject into the simulator.</param>
public sealed record SimulatorFaultRequest(string Kind);
