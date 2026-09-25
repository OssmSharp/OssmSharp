using OSSMDotNet.Models;
using OSSMDotNet.Simulator;

namespace OSSMDotNet.ApiService.Device;

/// <summary>
/// Executes privileged simulator-only fault controls at the host composition boundary.
/// </summary>
/// <param name="authority">The control authority supplying current desired settings.</param>
/// <param name="simulator">The simulator on which faults may be injected.</param>
public sealed class SimulatorFaultOperations(ControlAuthority authority, SimulatedDevice simulator)
{
    /// <summary>
    /// Applies one supported fault to the simulator, never to a physical device.
    /// </summary>
    /// <param name="request">The selected fault operation.</param>
    /// <exception cref="ControlRejectedException">The request is missing or its fault kind is unknown.</exception>
    public Task InjectAsync(SimulatorFaultRequest? request)
    {
        if (request is null)
            throw new ControlRejectedException("invalid-request");
        var desired = authority.Read().Snapshot.Desired;
        return request.Kind switch
        {
            "external-zero" => simulator.InjectExternalAsync(
                desired with { Speed = 0 }, CancellationToken.None),
            "external-depth" => simulator.InjectExternalAsync(
                desired with { Depth = Math.Min(100, desired.Depth + 1) }, CancellationToken.None),
            "delay" => simulator.SetDelayAsync(1800, CancellationToken.None),
            "stale" => simulator.SetStaleAsync(true, CancellationToken.None),
            "fresh" => simulator.SetStaleAsync(false, CancellationToken.None),
            "disconnect" => simulator.DisconnectAsync(CancellationToken.None),
            "connect" => simulator.ReconnectAsync(CancellationToken.None),
            _ => throw new ControlRejectedException("unknown-simulator-fault")
        };
    }
}
