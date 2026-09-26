namespace OssmSharp.Web.Contracts;

/// <summary>Represents the control limits safe to disclose to a guest.</summary>
/// <summary>
/// Describes the speed and depth limits visible to a guest.
/// </summary>
/// <param name="SpeedCap">The maximum speed permitted for guest input.</param>
/// <param name="MaximumDepth">The maximum depth permitted for guest input.</param>
public sealed record GuestLimits(int SpeedCap, int MaximumDepth);