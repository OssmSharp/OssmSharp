using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Owns one authenticated hub connection, allowing Stop alongside an ordinary command.
/// </summary>
/// <param name="factory">The factory for an isolated authenticated connection.</param>
/// <param name="options">The validated command deadline settings.</param>
internal sealed class ControlHubClient(ControlHubConnectionFactory factory, IOptions<ControlClientOptions> options) : IAsyncDisposable
{
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<HubConnection>? _connectionTask;
    private Task? _disposeTask;
    private int _disposed;
    private int _operationUsers;
    private int _state;

    /// <summary>
    /// Stops the owned hub connection and releases the command gate.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>
    /// Starts the authenticated connection once.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken, string? invitationToken = null)
    {
        if (!TryEnterOperation())
            throw new InvalidOperationException("The control connection has already closed.");

        try
        {
            if (Interlocked.CompareExchange(ref _state, -1, 0) != 0)
                throw new InvalidOperationException("The control connection has already started or closed.");

            var connection = invitationToken is null ? factory.Create() : factory.CreateForGuest(invitationToken);
            _connectionTask = Task.FromResult(connection);
            connection.Closed += OnClosedAsync;
            try
            {
                await connection.StartAsync(cancellationToken);
                if (Interlocked.CompareExchange(ref _state, 1, -1) != -1)
                    throw new OperationCanceledException("The control connection closed while it was starting.", cancellationToken);
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _state, 2);
                connection.Closed -= OnClosedAsync;
                _connectionTask = null;
                await connection.DisposeAsync();
                if (invitationToken is not null
                    && error.GetBaseException() is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                    throw new ControlClientException("invalid-invitation", error);
                if (error is HttpRequestException or System.Net.WebSockets.WebSocketException or IOException
                    || error is AggregateException aggregate
                    && aggregate.InnerExceptions.All(exception => exception is TransportFailedException))
                    throw new ControlClientException("connection-unavailable", error);
                throw;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Serializes ordinary commands separately from stop requests, preserving unknown outcomes on deadline expiry.
    /// </summary>
    /// <typeparam name="T">The service acknowledgement type.</typeparam>
    /// <param name="method">The hub method to invoke.</param>
    /// <param name="arguments">The command arguments.</param>
    /// <returns>The service response.</returns>
    /// <exception cref="ControlClientUnknownOutcomeException">The response deadline expired after dispatch.</exception>
    /// <exception cref="ControlClientException">The connection is unavailable or the service rejected the command.</exception>
    internal async Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryEnterOperation())
            throw new ControlClientException("connection-unavailable");

        try
        {
            var gate = method is "Stop" or "GuestStop" or "Leave" or "EndGuest" ? _stopGate : _commandGate;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var connection = _connectionTask is { } connectionTask ? await connectionTask : null;
                if (Volatile.Read(ref _state) != 1 || connection is null)
                    throw new ControlClientException("connection-unavailable");

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (method != "Connect")
                    deadline.CancelAfter(_commandTimeout);
                try
                {
                    return await connection.InvokeCoreAsync<T>(method, arguments, deadline.Token);
                }
                catch (HubException error)
                {
                    throw new ControlClientException(error.Message, error);
                }
                catch (InvalidOperationException error) when (
                    Volatile.Read(ref _state) != 1 || connection.State != HubConnectionState.Connected)
                {
                    throw new ControlClientException("connection-unavailable", error);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                {
                    if (method != "Submit")
                        Log.CommandOutcomeUnknown(factory.Logger, method);
                    throw new ControlClientUnknownOutcomeException(error);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _state, 2);
        if (Volatile.Read(ref _operationUsers) != 0)
        {
            await _operationsDrained.Task;
        }

        try
        {
            var connectionTask = Interlocked.Exchange(ref _connectionTask, null);
            if (connectionTask is not null)
            {
                var connection = await connectionTask;
                connection.Closed -= OnClosedAsync;
                try { await connection.StopAsync(); }
                finally { await connection.DisposeAsync(); }
            }
        }
        finally
        {
            _commandGate.Dispose();
            _stopGate.Dispose();
        }
    }

    private bool TryEnterOperation()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        Interlocked.Increment(ref _operationUsers);
        if (Volatile.Read(ref _disposed) == 0)
            return true;

        ExitOperation();
        return false;
    }

    private void ExitOperation()
    {
        if (Interlocked.Decrement(ref _operationUsers) == 0 && Volatile.Read(ref _disposed) != 0)
            _operationsDrained.TrySetResult();
    }

    private Task OnClosedAsync(Exception? error)
    {
        var previousState = Interlocked.Exchange(ref _state, 2);
        if (previousState == 2)
            return Task.CompletedTask;

        if (previousState == 1)
            Log.ConnectionLost(factory.Logger, error?.GetType().Name ?? "RemoteClose");

        return Task.CompletedTask;
    }
}
