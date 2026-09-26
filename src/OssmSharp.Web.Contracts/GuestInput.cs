namespace OssmSharp.Web.Contracts;

/// <summary>Represents one guest motion input sent to ControlService.</summary>
/// <summary>
/// Carries guest-safe desired motion bound to a grant, policy, epoch, and sequence.
/// </summary>
/// <param name="V">The input protocol version.</param>
/// <param name="Epoch">The authority epoch expected by the guest.</param>
/// <param name="PolicyVersion">The policy version expected by the guest.</param>
/// <param name="GrantId">The grant authorizing this input.</param>
/// <param name="Seq">The guest input sequence.</param>
/// <param name="Run">Whether the guest requests motion.</param>
/// <param name="Desired">The requested motion settings.</param>
public sealed record GuestInput(int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, ControlValues Desired);
