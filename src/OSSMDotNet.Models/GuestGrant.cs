namespace OSSMDotNet.Models;

/// <summary>Represents the guest-safe portion of a bounded motion grant.</summary>
public sealed record GuestGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);