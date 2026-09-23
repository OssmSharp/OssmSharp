namespace OSSMDotNet.GuestWeb.Tests;

/// <summary>
/// Captures a page request received by the loopback control hub.
/// </summary>
/// <param name="Method">The invoked hub method.</param>
/// <param name="Arguments">The arguments recorded for the invocation.</param>
internal sealed record PageHubInvocation(string Method, IReadOnlyList<object?> Arguments);
