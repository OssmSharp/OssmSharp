using Microsoft.JSInterop;

namespace OSSMDotNet.GuestWeb.Tests;

internal sealed class RecordedJsModule : IJSObjectReference
{
    private readonly object _gate = new();
    private readonly List<(string Identifier, object?[] Arguments)> _invocations = [];
    private readonly List<(string Identifier, int ExpectedCount, TaskCompletionSource Signal)> _waiters = [];
    private int _disposed;

    internal string? Invitation { get; set; }
    internal bool CopyResult { get; set; } = true;
    internal int DisposeCount { get; private set; }

    internal IReadOnlyList<(string Identifier, IReadOnlyList<object?> Arguments)> Invocations
    {
        get
        {
            lock (_gate)
            {
                return _invocations
                    .Select(static invocation => (invocation.Identifier, (IReadOnlyList<object?>)invocation.Arguments))
                    .ToArray();
            }
        }
    }

    internal int InvocationCount(string identifier)
    {
        lock (_gate)
            return _invocations.Count(invocation => invocation.Identifier == identifier);
    }

    internal Task WaitForInvocationCountAsync(
        string identifier,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_invocations.Count(invocation => invocation.Identifier == identifier) >= expectedCount)
                return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((identifier, expectedCount, signal));
            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(identifier, args);
        return identifier switch
        {
            "takeInvitation" => Result<TValue>(Invitation),
            "copy" => Result<TValue>(CopyResult),
            _ => Result<TValue>(default(TValue))
        };
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            DisposeCount++;
        return ValueTask.CompletedTask;
    }

    private void Record(string identifier, object?[]? arguments)
    {
        lock (_gate)
        {
            _invocations.Add((identifier, arguments?.ToArray() ?? []));
            var count = _invocations.Count(invocation => invocation.Identifier == identifier);
            foreach (var waiter in _waiters.Where(waiter =>
                waiter.Identifier == identifier && waiter.ExpectedCount <= count).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
    }

    private static ValueTask<TValue> Result<TValue>(object? value) => new((TValue)value!);
}
