namespace OSSMDotNet.ControlClient.Tests;

internal sealed record HubInvocation(string Method, IReadOnlyList<object?> Arguments);
