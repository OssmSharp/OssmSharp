namespace OSSMDotNet.Models;

/// <summary>Represents control values carried through the control-service contract.</summary>
public sealed record ControlValues(int Pattern, int Speed, int Stroke, int Depth, int Sensation);
