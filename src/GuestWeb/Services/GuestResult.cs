using OSSMDotNet.Web.SessionContracts;

namespace OSSMDotNet.Web.GuestWeb.Services;

public sealed record GuestResult(bool Ok, string Code, GuestStatus? State, long Revision = 0);
