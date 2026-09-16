namespace OSSMDotNet.Models;

/// <summary>Reports whether the named-pipe handshake was accepted.</summary>
public sealed record PipeWelcome(bool Accepted);