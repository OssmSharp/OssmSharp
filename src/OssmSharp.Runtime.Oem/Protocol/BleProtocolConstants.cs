namespace OssmSharp.Runtime.Oem.Protocol;

/// <summary>
/// Defines the RAD BLE v1 service and characteristic UUIDs.
/// </summary>
/// <remarks>
/// A BLE device exposes services, each a UUID-identified group of characteristics. A characteristic is a single
/// value that clients can read, write, or subscribe to for notifications or indications.
/// Each characteristic UUID here replaces the fourth group of the <see cref="Service"/> UUID.
/// </remarks>
internal static class BleProtocolConstants
{
    /// <summary>
    /// Identifies the service that contains every protocol characteristic; devices advertise it, so scans filter on it.
    /// </summary>
    public const string Service = "522b443a-4f53-534d-0001-420badbabe69";

    /// <summary>
    /// Identifies the readable JSON document naming the protocol, its version, and message limits.
    /// </summary>
    public const string ProtocolInfo = "522b443a-4f53-534d-0002-420badbabe69";

    /// <summary>
    /// Identifies the characteristic that accepts JSON requests written with response.
    /// </summary>
    public const string Request = "522b443a-4f53-534d-1000-420badbabe69";

    /// <summary>
    /// Identifies the characteristic that indicates JSON responses, correlated to requests by <c>id</c>.
    /// </summary>
    public const string Response = "522b443a-4f53-534d-1100-420badbabe69";

    /// <summary>
    /// Identifies the Compact State characteristic, which reports mode and lease metadata but not motion controls.
    /// </summary>
    public const string State = "522b443a-4f53-534d-2000-420badbabe69";

    /// <summary>
    /// Identifies the optional characteristic that notifies binary sensor frames, including motion observations.
    /// </summary>
    public const string SensorStream = "522b443a-4f53-534d-2300-420badbabe69";
}
