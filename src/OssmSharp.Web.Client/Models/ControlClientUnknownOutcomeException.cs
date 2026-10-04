namespace OssmSharp.Web.Client.Models;

/// <summary>
/// Indicates a command deadline elapsed after dispatch, so service acceptance is unknown.
/// </summary>
/// <remarks>
/// Wait for fresh service state before deciding what to do next; resending the command may duplicate its effect.
/// </remarks>
/// <param name="innerException">The deadline failure that left acceptance uncertain.</param>
public sealed class RpcClientUnknownOutcomeException(Exception innerException)
    : Exception("The control command outcome is unknown. Wait for the next service state update; do not resend it.", innerException);
