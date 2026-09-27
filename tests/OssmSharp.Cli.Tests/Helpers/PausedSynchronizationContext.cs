namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class PausedSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
    private bool _resumed;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_pending)
        {
            if (!_resumed)
            {
                _pending.Enqueue((callback, state));
                return;
            }
        }

        base.Post(callback, state);
    }

    public void Dispose()
    {
        lock (_pending)
        {
            _resumed = true;
            while (_pending.TryDequeue(out var work))
            {
                base.Post(work.Callback, work.State);
            }
        }
    }
}
