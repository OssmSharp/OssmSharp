namespace OSSMDotNet.Models;

/// <summary>Represents the requested device settings for one motion command.</summary>
public sealed record DesiredState(int Pattern, int Speed, int Stroke, int Depth, int Sensation)
{
    public static DesiredState Stopped { get; } = new(0, 0, 10, 20, 50);
}
