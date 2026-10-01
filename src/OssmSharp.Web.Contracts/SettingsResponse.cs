namespace OssmSharp.Web.Contracts;

/// <summary>
/// Reports the runtime result of one settings submission.
/// </summary>
/// <param name="Correlation">The caller-provided request identifier.</param>
/// <param name="Status">The runtime result or API rejection category.</param>
/// <param name="CommandsWritten">The number of primitive settings writes completed by the runtime.</param>
/// <param name="ErrorCode">A safe error code when the request failed or was rejected.</param>
public sealed record SettingsResponse(
    string Correlation,
    SettingsResponseStatus Status,
    int CommandsWritten,
    string? ErrorCode = null);
