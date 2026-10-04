namespace OssmSharp.Web.Client.Models;

/// <summary>
/// Indicates that an RPC response was not received before the request deadline.
/// </summary>
/// <param name="innerException">The underlying response deadline failure.</param>
public sealed class RpcClientRequestTimeoutException(Exception innerException)
    : Exception("The request timed out. It may have been processed by the server.", innerException);
