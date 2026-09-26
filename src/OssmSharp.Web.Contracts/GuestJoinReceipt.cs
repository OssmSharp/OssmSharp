namespace OssmSharp.Web.Contracts;

/// <summary>Confirms a private guest admission without exposing its server-side proof.</summary>
/// <param name="Code">The result code for the admission.</param>
/// <param name="Correlation">The identifier correlating the receipt with the join request.</param>
public sealed record GuestJoinReceipt(string Code, string Correlation) : IControlResponse
{
    /// <summary>Gets whether the invitation admission was accepted.</summary>
    public bool Accepted { get; init; } = true;
}
