namespace OSSMDotNet.Models;

/// <summary>Confirms that one control command was accepted without returning control state.</summary>
public sealed record ControlAcknowledgement(string Code, string Correlation) : IControlResponse
{
    /// <summary>Gets whether the requested command was accepted.</summary>
    public bool Accepted { get; init; } = true;
}
