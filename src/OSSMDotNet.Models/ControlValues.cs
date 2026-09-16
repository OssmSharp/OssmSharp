namespace OSSMDotNet.Models;

/// <summary>Represents control values carried across the guest pipe boundary.</summary>
public sealed record ControlValues(int Pattern, int Speed, int Stroke, int Depth, int Sensation);