namespace OssmSharp.Web.Contracts;

/// <summary>
/// Contains the private invitation token shown only to the user.
/// </summary>
/// <param name="Token">The token a guest presents to join.</param>
public sealed record GuestInvitation(string Token);
