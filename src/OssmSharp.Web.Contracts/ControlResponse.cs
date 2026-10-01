namespace OssmSharp.Web.Contracts;

/// <summary>
/// Reports whether a UI control request was accepted.
/// </summary>
/// <param name="Correlation">The caller-provided request identifier.</param>
/// <param name="Code">The stable result code.</param>
/// <param name="Accepted">Whether the request was accepted.</param>
public sealed record ControlResponse(string Correlation, string Code, bool Accepted);
