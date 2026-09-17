namespace OSSMDotNet.ControlClient;

/// <summary>Represents a control-service operation rejected before it could be accepted.</summary>
public sealed class ControlClientException(string code, Exception? innerException = null)
    : Exception($"Control service rejected the operation: {code}.", innerException)
{
    /// <summary>Gets the stable service rejection code.</summary>
    public string Code { get; } = code;
}
