namespace OssmSharp.Web.Api.Tests.Control.Models;

internal sealed record AdmissionState(bool Admitted = false, int Tab = 0, bool Accepted = false, string Code = "");
