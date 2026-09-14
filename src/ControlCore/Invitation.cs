namespace OSSMDotNet.Web.ControlCore;

public sealed record Invitation(string SessionId, string Url, DateTimeOffset ExpiresAt, bool LocalOnly);
