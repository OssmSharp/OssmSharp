namespace OssmSharp.Web.Api.Models;

/// <summary>
/// Holds server-side guest admission needed to scope status and report transport loss.
/// </summary>
/// <param name="LogicalConnectionId">The guest's logical connection identifier.</param>
/// <param name="SessionId">The session to which the guest was admitted.</param>
/// <param name="Proof">The private admission proof.</param>
internal sealed record HubGuestSession(string LogicalConnectionId, string SessionId, string Proof);
