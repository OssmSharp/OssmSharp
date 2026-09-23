namespace OSSMDotNet.ControlClient.Tests;

/// <summary>
/// Captures the name and arguments of a client request received by the loopback hub.
/// </summary>
/// <param name="Method">The invoked hub method.</param>
/// <param name="Arguments">The arguments recorded for the invocation.</param>
internal sealed record HubInvocation(string Method, IReadOnlyList<object?> Arguments);
