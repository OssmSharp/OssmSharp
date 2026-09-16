namespace OSSMDotNet.Models;

/// <summary>Authenticates a guest process when it opens the named-pipe connection.</summary>
public sealed record PipeHello(int V, string ServiceToken, string ConnectionId);