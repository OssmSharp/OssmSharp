namespace OSSMDotNet.Models;

/// <summary>Identifies the exact control-service authority state required to perform an operator action.</summary>
/// <summary>
/// Binds an operator mutation to the observed service instance and authority revision.
/// </summary>
/// <param name="ServiceInstanceId">The instance that produced the observed authority state.</param>
/// <param name="AuthorityRevision">The observed authority revision.</param>
/// <param name="GuestAdmission">The guest admission expected by the operator.</param>
public sealed record AuthorityPrecondition(string ServiceInstanceId, long AuthorityRevision, GuestAdmission GuestAdmission);
