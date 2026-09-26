namespace OssmSharp.Web.Contracts;

/// <summary>Confirms that one control command was accepted without returning control state.</summary>
/// <param name="Code">The acknowledgement code returned to the caller.</param>
/// <param name="Correlation">The identifier correlating this acknowledgement with its command.</param>
public sealed record ControlAcknowledgement(string Code, string Correlation) : IControlResponse
{
    /// <summary>Gets whether the requested command was accepted.</summary>
    public bool Accepted { get; init; } = true;
}
