namespace OssmSharp.Web.Client.Models;

/// <summary>
/// Represents a control-service operation rejected before it could be accepted.
/// </summary>
/// <param name="code">The stable service rejection code.</param>
/// <param name="innerException">The underlying failure, if available.</param>
public sealed class ApiClientException(string code, Exception? innerException = null)
    : Exception($"Control service rejected the operation: {code}.", innerException)
{
    /// <summary>
    /// Gets the stable service rejection code.
    /// </summary>
    public string Code { get; } = code;
}
