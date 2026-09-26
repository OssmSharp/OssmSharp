namespace OssmSharp.Web.Application.Models;

/// <summary>Indicates that an operation was rejected by the controller's safety policy.</summary>
/// <param name="code">The policy rejection code.</param>
public sealed class ControlRejectedException(string code) : Exception(code)
{
    /// <summary>
    /// Gets the policy rejection code for the failed operation.
    /// </summary>
    public string Code { get; } = code;
}
