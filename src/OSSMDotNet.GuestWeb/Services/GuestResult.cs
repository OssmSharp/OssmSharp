using OSSMDotNet.Models;

namespace OSSMDotNet.GuestWeb.Services;

/// <summary>Represents the guest client's result for one restricted pipe operation.</summary>
public sealed record GuestResult(bool Ok, string Code, GuestStatus? State, long Revision = 0);
