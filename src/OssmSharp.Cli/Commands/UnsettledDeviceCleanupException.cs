namespace OssmSharp.Cli.Commands;

internal sealed class UnsettledDeviceCleanupException(string message, Exception innerException)
    : IOException(message, innerException);
