namespace OSSMDotNet.ControlCore.Tests.Helpers;

/// <summary>
/// Records device writes and lets tests control their completion and reported state.
/// </summary>
internal sealed class FakeBridge : IOssmDevice
{
    private static readonly DeviceInformation Information = new(
        "test simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]);
    private readonly List<BridgeCall> _calls = [];
    private int _active;
    private bool _connected;
    private MotionSettings _desired = TestMotion.Settings();

    public event Action<DeviceConnectionState>? ConnectionStateChanged;

    public event Action<OneOf.OneOf<DeviceState, Exception>>? DeviceStateChanged
    {
        add { _deviceStateChanged += value; SubscriberCount++; }
        remove { _deviceStateChanged -= value; SubscriberCount--; }
    }

    private event Action<OneOf.OneOf<DeviceState, Exception>>? _deviceStateChanged;

    /// <summary>
    /// Gets or sets whether writes settle immediately; disable to test pending device I/O.
    /// </summary>
    public bool AutoComplete { get; set; } = true;

    /// <summary>
    /// Gets or sets whether settling a write publishes the resulting device state.
    /// </summary>
    public bool PublishOnCompletion { get; set; } = true;
    public bool RetractOnStop { get; set; }
    public bool Connected
    {
        get => _connected;
        set
        {
            if (_connected == value)
                return;
            _connected = value;
            ConnectionStateChanged?.Invoke(ConnectionState);
        }
    }

    public string Profile { get; set; } = Information.ProfileId;
    public double Position { get; set; }
    public int? ReportedSpeed { get; set; }
    public double Velocity { get; set; }
    public Action? BeforeStatePublished { get; set; }
    public int SubscriberCount { get; private set; }
    public int MaxActive { get; private set; }
    /// <summary>
    /// Gets recorded writes, including pending calls when automatic completion is disabled.
    /// </summary>
    public IReadOnlyList<BridgeCall> Calls => _calls;
    public DeviceConnectionState ConnectionState => Connected
        ? DeviceConnectionState.Connected
        : DeviceConnectionState.Disconnected;
    public DeviceInformation Device => Information with { ProfileId = Profile };

    public Task SetMotionAsync(MotionSettings desired, CancellationToken cancellationToken) =>
        Start(desired.Speed == 0 ? "Stop" : "Write", desired);

    private Task Start(string kind, MotionSettings desired)
    {
        var call = new BridgeCall(kind, desired);
        _calls.Add(call);
        _active++;
        MaxActive = Math.Max(MaxActive, _active);
        if (AutoComplete) Complete(call);
        return call.Completion.Task;
    }

    /// <summary>
    /// Settles the oldest pending write and optionally publishes its state.
    /// </summary>
    public void CompleteNext() => Complete(_calls.First(call => !call.Completion.Task.IsCompleted));

    /// <summary>
    /// Fails the oldest pending device write without publishing new state.
    /// </summary>
    /// <param name="error">The failure returned to the caller waiting on the write.</param>
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
        _desired = call.Desired;
        if (PublishOnCompletion) Publish();
        _active--;
        call.Completion.SetResult();
    }

    /// <summary>
    /// Publishes an external settings change and raises a mismatch error when control settings changed.
    /// </summary>
    /// <param name="desired">The settings reported by the external device.</param>
    public void External(MotionSettings desired)
    {
        var previous = _desired;
        _desired = desired;
        Publish();
        if (previous.Speed != desired.Speed)
        {
            _deviceStateChanged?.Invoke(new UnexpectedSpeedChangeException(
                previous.Speed,
                desired.Speed,
                null,
                DateTime.UtcNow));
        }
        else if (!SameControlSettings(previous, desired))
        {
            _deviceStateChanged?.Invoke(new UnexpectedMotionChangeException(
                previous,
                desired,
                null,
                DateTime.UtcNow));
        }
    }

    public void Publish(bool advanceSequence = true)
    {
        if (!Connected)
            return;
        BeforeStatePublished?.Invoke();
        _deviceStateChanged?.Invoke(new DeviceState
        {
            Timestamp = DateTime.UtcNow,
            MotionSettings = _desired with
            {
                Speed = _desired.Speed == 0 ? 0 : ReportedSpeed ?? _desired.Speed,
                Position = Position,
            },
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = RetractOnStop && _desired.Speed == 0 ? 1 : Velocity,
        });
    }

    public void ClearCalls() => _calls.Clear();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool SameControlSettings(MotionSettings first, MotionSettings second) =>
        first.Speed == second.Speed &&
        first.Stroke == second.Stroke &&
        first.Sensation == second.Sensation &&
        first.Depth == second.Depth &&
        first.Buffer == second.Buffer &&
        first.Pattern == second.Pattern;
}
