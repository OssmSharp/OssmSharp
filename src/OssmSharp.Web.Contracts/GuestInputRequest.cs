namespace OssmSharp.Web.Contracts;

/// <summary>Contains one guest motion request accepted only for its server-bound admission.</summary>
/// <summary>
/// Associates a guest input with its response correlation identifier.
/// </summary>
/// <param name="Correlation">The identifier echoed in the response.</param>
/// <param name="Input">The grant-bound guest motion request.</param>
public sealed record GuestInputRequest(string Correlation, GuestInput Input);
