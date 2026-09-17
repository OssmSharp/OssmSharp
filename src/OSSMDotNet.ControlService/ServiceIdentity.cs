namespace OSSMDotNet.ControlService;

/// <summary>Represents a service identity authenticated by its configured service key.</summary>
public sealed record ServiceIdentity(string Name, string Role);
