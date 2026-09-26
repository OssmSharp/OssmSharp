namespace OssmSharp.Web.Application.Models;

/// <summary>Represents a guest motion request bound to a grant, epoch, policy, and sequence.</summary>
/// <param name="V">The input protocol version.</param>
/// <param name="Epoch">The authority epoch expected by the guest.</param>
/// <param name="PolicyVersion">The policy version expected by the guest.</param>
/// <param name="GrantId">The grant authorizing this input.</param>
/// <param name="Seq">The guest input sequence.</param>
/// <param name="Run">Whether the guest requests motion.</param>
/// <param name="Desired">The requested device motion settings.</param>
public sealed record MotionInput(
    int V, string Epoch, long PolicyVersion, string GrantId, long Seq, bool Run, MotionSettings Desired);
