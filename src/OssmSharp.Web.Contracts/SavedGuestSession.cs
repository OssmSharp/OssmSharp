namespace OssmSharp.Web.Contracts;

/// <summary>
/// Describes a validated saved guest session without exposing its credential or device state.
/// </summary>
/// <param name="Connected">Whether a controller is currently attached. Another connection cannot replace it.</param>
/// <param name="ExpiresAt">The absolute session expiry; activity never extends it.</param>
/// <param name="CanAttach">Whether the controller slot is free and required Stop/reset cleanup has settled.</param>
public sealed record SavedGuestSession(bool Connected, DateTimeOffset ExpiresAt, bool CanAttach);
