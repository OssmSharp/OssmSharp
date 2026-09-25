namespace OSSMDotNet.ApiService.Authentication;

/// <summary>
/// Represents a service identity authenticated by its configured service key.
/// </summary>
/// <param name="Name">The authenticated web-service name.</param>
/// <param name="Role">The authorization role granted to that service.</param>
public sealed record ServiceIdentity(string Name, string Role);
