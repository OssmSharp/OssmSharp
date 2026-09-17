namespace OSSMDotNet.Models;

/// <summary>Confirms a private guest admission without exposing its server-side proof.</summary>
public sealed record GuestJoinReceipt(string Code, string Correlation) : IControlResponse
{
    /// <summary>Gets whether the invitation admission was accepted.</summary>
    public bool Accepted { get; init; } = true;
}
