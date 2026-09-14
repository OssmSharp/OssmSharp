namespace OSSMDotNet.DeviceControl;

public sealed record DesiredState(int Pattern, int Speed, int Stroke, int Depth, int Sensation)
{
    public static DesiredState Stopped { get; } = new(0, 0, 10, 20, 50);
}
