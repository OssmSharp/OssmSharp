namespace OSSMDotNet.ControlCore.Models;

/// <summary>
/// Tracks one device write and the generation, connection, and sample that own its outcome.
/// </summary>
/// <param name="kind">The kind of device write.</param>
/// <param name="desired">The settings submitted with the write.</param>
/// <param name="startedAt">The monotonic timestamp when the operation began.</param>
/// <param name="generation">The control generation that owns the write.</param>
/// <param name="sampleSequence">The last device sample preceding the write.</param>
/// <param name="connection">The device connection that owns the write.</param>
/// <param name="forEnvelope">Whether the write belongs to the motion envelope.</param>
internal sealed class Operation(OperationKind kind, MotionSettings desired, long startedAt,
    long generation, long sampleSequence, long connection, bool forEnvelope)
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _task;

    /// <summary>
    /// Gets the kind of device write.
    /// </summary>
    public OperationKind Kind { get; } = kind;
    /// <summary>
    /// Gets the settings submitted with the write.
    /// </summary>
    public MotionSettings Desired { get; } = desired;
    /// <summary>
    /// Gets the monotonic timestamp when the operation began.
    /// </summary>
    public long StartedAt { get; } = startedAt;
    /// <summary>
    /// Gets the control generation that owns the write.
    /// </summary>
    public long Generation { get; } = generation;
    /// <summary>
    /// Gets the device sample sequence preceding the write.
    /// </summary>
    public long SampleSequence { get; } = sampleSequence;
    /// <summary>
    /// Gets the device connection that owns the write.
    /// </summary>
    public long Connection { get; } = connection;
    /// <summary>
    /// Gets whether the write belongs to the motion envelope.
    /// </summary>
    public bool ForEnvelope { get; } = forEnvelope;
    /// <summary>
    /// Gets or sets whether the writer has claimed this operation.
    /// </summary>
    public bool Claimed { get; set; }
    /// <summary>
    /// Gets or sets whether the operation exceeded its observation deadline.
    /// </summary>
    public bool TimedOut { get; set; }
    /// <summary>
    /// Gets or sets the monotonic completion timestamp when the device write settles.
    /// </summary>
    public long? CompletedAt { get; set; }
    /// <summary>
    /// Gets the device write, or waits for it to be started.
    /// </summary>
    public Task Task => _task ?? _started.Task;

    /// <summary>
    /// Attaches the issued device write and releases observers waiting for dispatch.
    /// </summary>
    /// <param name="task">The issued device write.</param>
    public void Start(Task task)
    {
        _task = task;
        _started.TrySetResult();
    }
}
