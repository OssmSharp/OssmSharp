using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore.Tests;

internal sealed class FakeBridge : IDeviceTransport
{
    private readonly List<BridgeCall> _calls = [];
    private Action<DeviceState>? _received;
    private long _sequence;
    private long _revision;
    private long? _command;
    private int _active;
    private MotionSettings _desired = MotionSettings.Stopped;
    private bool _enabled;
    private bool _moving;

    public event Action<DeviceState>? SampleReceived
    {
        add { _received += value; SubscriberCount++; }
        remove { _received -= value; SubscriberCount--; }
    }

    public bool AutoComplete { get; set; } = true;
    public bool PublishOnCompletion { get; set; } = true;
    public bool RetractOnStop { get; set; }
    public bool Connected { get; set; } = true;
    public string Profile { get; set; } = "ossm-rs-sim-v1";
    public double Position { get; set; }
    public double Velocity { get; set; }
    public int SubscriberCount { get; private set; }
    public int MaxActive { get; private set; }
    public IReadOnlyList<BridgeCall> Calls => _calls;

    public Task SetMotionAsync(long commandId, MotionSettings desired, CancellationToken cancellationToken) =>
        Start("Write", commandId, desired);
    public Task StopAsync(long commandId, CancellationToken cancellationToken) =>
        Start("Stop", commandId, null);

    private Task Start(string kind, long commandId, MotionSettings? desired)
    {
        var call = new BridgeCall(kind, commandId, desired);
        _calls.Add(call);
        _active++;
        MaxActive = Math.Max(MaxActive, _active);
        if (AutoComplete) Complete(call);
        return call.Completion.Task;
    }

    public void CompleteNext() => Complete(_calls.First(call => !call.Completion.Task.IsCompleted));

    public void FailNext(Exception error)
    {
        var call = _calls.First(call => !call.Completion.Task.IsCompleted);
        _active--;
        call.Completion.SetException(error);
    }

    public void CompleteAll()
    {
        foreach (var call in _calls.Where(call => !call.Completion.Task.IsCompleted).ToArray())
            Complete(call);
    }

    private void Complete(BridgeCall call)
    {
        var desired = call.Desired ?? _desired with { Speed = 0 };
        if (_desired != desired) _revision++;
        _desired = desired;
        _command = call.CommandId;
        _enabled = desired.Speed != 0;
        _moving = _enabled || (call.Kind == "Stop" && RetractOnStop);
        if (PublishOnCompletion) Publish();
        _active--;
        call.Completion.SetResult();
    }

    public void External(MotionSettings desired, long? commandId = null)
    {
        if (_desired != desired) _revision++;
        _desired = desired;
        _command = commandId;
        _enabled = desired.Speed > 0;
        _moving = _enabled;
        Publish();
    }

    public void Publish(bool advanceSequence = true)
    {
        if (advanceSequence) _sequence++;
        _received?.Invoke(new(_sequence, _revision, _command, _desired, _enabled, _moving, Position, Velocity, Connected, Profile));
    }

    public void ClearCalls() => _calls.Clear();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
