namespace OssmSharp.Device.Primitives;

/// <summary>Indicates that native cleanup did not settle; continued device ownership may be unknown.</summary>
public sealed class UnsettledDeviceCleanupException(string message, Exception innerException)
    : IOException(message, innerException);
