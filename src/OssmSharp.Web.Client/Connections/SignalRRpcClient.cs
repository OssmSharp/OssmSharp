using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OssmSharp.Web.Client.Models;

namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Owns one authenticated hub connection, allowing Stop alongside an ordinary command.
/// </summary>
/// <param name="factory">The factory for an isolated authenticated connection.</param>
/// <param name="options">The validated request deadline settings.</param>
/// <param name="timeProvider">The clock for ordinary request deadlines.</param>
/// <param name="logger">The logger for connection and request results.</param>
internal sealed class SignalRRpcClient(IHubConnectionFactory factory, IOptions<RpcClientOptions> options, TimeProvider timeProvider, ILogger<SignalRRpcClient>? logger = null)
    : IRpcClient
{
    private readonly ILogger _logger = logger ?? NullLogger<SignalRRpcClient>.Instance;
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeSync = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<HubConnection>? _connectionTask;
    private Task? _disposeTask;
    private int _disposed;
    private int _operationUsers;
    private int _state;

    /// <summary>
    /// Cancels owned response waits, drains active operations, and closes the hub connection.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>
    /// Starts the authenticated connection once.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken, string? invitationToken = null)
    {
        if (!TryEnterOperation())
            throw new InvalidOperationException("The control connection has already closed.");

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (Interlocked.CompareExchange(ref _state, -1, 0) != 0)
                throw new InvalidOperationException("The control connection has already started or closed.");

            var connection = invitationToken is null ? factory.Create() : factory.CreateForGuest(invitationToken);
            _connectionTask = Task.FromResult(connection);
            connection.Closed += OnClosedAsync;
            try
            {
                await connection.StartAsync(startup.Token);
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
                    throw new ApiClientException("invalid-invitation", error);
                if (error is HttpRequestException or System.Net.WebSockets.WebSocketException or IOException
                    || error is AggregateException aggregate
                    && aggregate.InnerExceptions.All(exception => exception is TransportFailedException))
                    throw new ApiClientException("connection-unavailable", error);
                throw;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Serializes ordinary requests separately from Stop; device-duration operations use caller and connection cancellation.
    /// </summary>
    /// <typeparam name="T">The service acknowledgement type.</typeparam>
    /// <param name="method">The hub method to invoke.</param>
    /// <param name="arguments">The command arguments.</param>
    /// <returns>The service response.</returns>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expired after dispatch.</exception>
    /// <exception cref="ApiClientException">The connection is unavailable or the API rejected the request.</exception>
    public async Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryEnterOperation())
            throw new ApiClientException("connection-unavailable");

        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var gate = method is "Stop" or "GuestStop" or "Leave" or "EndGuest" ? _stopGate : _requestGate;
            await gate.WaitAsync(operation.Token);
            try
            {
                var connection = _connectionTask is { } connectionTask ? await connectionTask : null;
                if (Volatile.Read(ref _state) != 1 || connection is null)
                    throw new ApiClientException("connection-unavailable");

                using var timeout = new CancellationTokenSource(
                    method is "Connect" or "Submit" or "ResetPosition" or "GuestResetPosition" ? Timeout.InfiniteTimeSpan : _requestTimeout, timeProvider);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, timeout.Token);
                try
                {
                    return await connection.InvokeCoreAsync<T>(method, arguments, deadline.Token);
                }
                catch (HubException error)
                {
                    throw new ApiClientException(error.Message, error);
                }
                catch (InvalidOperationException error) when (
                    Volatile.Read(ref _state) != 1 || connection.State != HubConnectionState.Connected)
                {
                    throw new ApiClientException("connection-unavailable", error);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested
                    && (Volatile.Read(ref _state) != 1 || connection.State != HubConnectionState.Connected))
                {
                    throw new ApiClientException("connection-unavailable", error);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    Log.RequestOutcomeUnknown(_logger, method);
                    throw new RpcClientRequestTimeoutException(error);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            throw new ApiClientException("connection-unavailable", error);
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
        await _lifetime.CancelAsync();
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
            _requestGate.Dispose();
            _stopGate.Dispose();
            _lifetime.Dispose();
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
            Log.ConnectionLost(_logger, error?.GetType().Name ?? "RemoteClose");

        return Task.CompletedTask;
    }
}
