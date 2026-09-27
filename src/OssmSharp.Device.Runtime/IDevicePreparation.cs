namespace OssmSharp.Device.Runtime;

/// <summary>
/// Provider-specific, non-navigating readiness preparation under the shared operation owner.
/// An issued native operation must settle using the settlement token even after revocation.
/// </summary>
public interface IDevicePreparation
{
    /// <summary>Prepares the connected device without granting motion authority.</summary>
    Task PrepareAsync(CancellationToken revocationToken, CancellationToken settlementToken);
}
