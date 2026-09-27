namespace OssmSharp.Device.Runtime;

/// <summary>
/// Dispatches an execution-owner safety Stop independently of ordinary motion-tuple transitions.
/// </summary>
public interface IDeviceSafetyStop
{
    /// <summary>
    /// Issues speed zero while retaining the device's serialized write and settlement ownership.
    /// </summary>
    /// <param name="reportedZero">A validated reported tuple with speed zero; only speed may be written.</param>
    /// <param name="cancellationToken">Cancels work before issuance without treating cancellation as settlement.</param>
    /// <returns>Completion after the issued zero-speed write settles, not proof of physical stopping.</returns>
    /// <exception cref="ArgumentException">The supplied tuple is invalid or has nonzero speed.</exception>
    Task SetSafetyStopAsync(MotionSettings reportedZero, CancellationToken cancellationToken);
}
