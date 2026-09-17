namespace OSSMDotNet.Models;

/// <summary>Represents the operator-only snapshot delivered through the live control hub.</summary>
public sealed record OperatorLiveControlStatus(ControlAuthoritySnapshot Authority, Invitation Invitation);
