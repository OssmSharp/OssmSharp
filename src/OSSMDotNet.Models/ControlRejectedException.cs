namespace OSSMDotNet.Models;

/// <summary>Indicates that an operation was rejected by the controller's safety policy.</summary>
public sealed class ControlRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}