namespace OSSMDotNet.Models;

/// <summary>Describes a control-service response with a stable acceptance result and code.</summary>
public interface IControlResponse
{
    /// <summary>Gets whether the requested operation was accepted.</summary>
    bool Accepted { get; }

    /// <summary>Gets the stable application result code.</summary>
    string Code { get; }
}
