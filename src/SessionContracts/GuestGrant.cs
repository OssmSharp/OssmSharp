namespace OSSMDotNet.Web.SessionContracts;

public sealed record GuestGrant(string Id, string Epoch, long PolicyVersion, int ValidForMs);
