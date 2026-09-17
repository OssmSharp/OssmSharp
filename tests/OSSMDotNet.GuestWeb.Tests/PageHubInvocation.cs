namespace OSSMDotNet.GuestWeb.Tests;

internal sealed record PageHubInvocation(string Method, IReadOnlyList<object?> Arguments);
