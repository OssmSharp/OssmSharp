namespace OSSMDotNet.Models;

/// <summary>Represents the reusable invitation issued by ControlService.</summary>
public sealed record Invitation(string SessionId, string Url, DateTimeOffset? ExpiresAt, bool LocalOnly);
