namespace OssmSharp.Device.Primitives.Models;

/// <summary>
/// Identifies one discovered device within its provider's lifetime.
/// </summary>
/// <remarks>Public metadata is not a native handle or a persistent device credential.</remarks>
public abstract record DeviceIdentifier
{
    /// <summary>Gets the provider-defined opaque locator.</summary>
    public abstract string Id { get; }

    /// <summary>Gets the advertised display name captured during discovery.</summary>
    public abstract string Name { get; }
}
