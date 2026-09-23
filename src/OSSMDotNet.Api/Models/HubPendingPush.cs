namespace OSSMDotNet.Api.Models;

/// <summary>
/// Holds one bounded callback awaiting delivery at a state version.
/// </summary>
/// <param name="Method">The hub callback method to invoke.</param>
/// <param name="Payload">The callback payload.</param>
/// <param name="Version">The state version associated with the callback.</param>
internal sealed record HubPendingPush(string Method, object Payload, long Version);
