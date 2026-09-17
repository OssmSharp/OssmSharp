namespace OSSMDotNet.Models;

/// <summary>Identifies the exact control-service authority state required to perform an operator action.</summary>
public sealed record AuthorityPrecondition(string ServiceInstanceId, long AuthorityRevision, GuestAdmission GuestAdmission);
