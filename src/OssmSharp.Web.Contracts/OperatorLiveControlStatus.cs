namespace OssmSharp.Web.Contracts;

/// <summary>Represents the operator-only snapshot delivered through the live control hub.</summary>
/// <summary>
/// Combines the operator authority snapshot with the current invitation.
/// </summary>
/// <param name="Authority">The trusted controller state and mutation precondition.</param>
/// <param name="Invitation">The current guest invitation.</param>
public sealed record OperatorLiveControlStatus(ControlAuthoritySnapshot Authority, Invitation Invitation);
