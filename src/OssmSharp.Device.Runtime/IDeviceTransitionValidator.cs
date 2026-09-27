namespace OssmSharp.Device.Runtime;

/// <summary>
/// Validates an entire proposed device transition against provider-specific constraints
/// before the execution owner accepts work.
/// </summary>
public interface IDeviceTransitionValidator
{
    /// <summary>
    /// Rejects unsupported intermediate or final tuples without issuing device I/O.
    /// </summary>
    /// <param name="reported">The latest validated device state at acceptance.</param>
    /// <param name="planned">The ordered tuples, including the final request.</param>
    void ValidateTransition(MotionSettings reported, IReadOnlyList<MotionSettings> planned);
}
