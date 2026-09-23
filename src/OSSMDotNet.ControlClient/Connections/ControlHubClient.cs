using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlClient.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Connections;

/// <summary>
/// Serializes one authenticated hub connection's callback, command, and terminal lifecycle.
/// </summary>
/// <param name="factory">The factory for an isolated authenticated connection.</param>
/// <param name="options">The validated command deadline settings.</param>
internal sealed class ControlHubClient(ControlHubConnectionFactory factory, IOptions<ControlClientOptions> options) : IAsyncDisposable
{
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(options.Value.CommandTimeoutSeconds);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _callbackGate = new(1, 1);
    private Task<HubConnection>? _connectionTask;
    private Func<Task>? _closed;
    private int _state;

    /// <summary>
    /// Gets whether the connection is active and accepting commands.
    /// </summary>
    internal bool IsConnected => Volatile.Read(ref _state) == 1;

    /// <summary>
    /// Registers callbacks before connecting and reports a later terminal close once.
    /// </summary>
    /// <param name="registerCallbacks">Registers the single callback set on the new connection.</param>
    /// <param name="closed">Handles terminal connection loss.</param>
    internal async Task StartAsync(Action<HubConnection> registerCallbacks, Func<Task> closed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registerCallbacks);
        ArgumentNullException.ThrowIfNull(closed);
        if (Interlocked.CompareExchange(ref _state, -1, 0) != 0)
            throw new InvalidOperationException("The control connection has already started or closed.");

        var connection = factory.Create();
        _connectionTask = Task.FromResult(connection);
        _closed = closed;
        connection.Closed += OnClosedAsync;
        try
        {
            registerCallbacks(connection);
            await connection.StartAsync(cancellationToken);
            if (Interlocked.CompareExchange(ref _state, 1, -1) != -1)
                throw new OperationCanceledException("The control connection closed while it was starting.", cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref _state, 2);
            connection.Closed -= OnClosedAsync;
            await connection.DisposeAsync();
            _connectionTask = null;
            throw;
        }
    }

    /// <summary>
    /// Invokes one command after earlier commands settle, preserving unknown outcomes on deadline expiry.
    /// </summary>
    /// <typeparam name="T">The service acknowledgement type.</typeparam>
    /// <param name="method">The hub method to invoke.</param>
    /// <param name="arguments">The command arguments.</param>
    /// <returns>The accepted service acknowledgement.</returns>
    /// <exception cref="ControlClientUnknownOutcomeException">The response deadline expired after dispatch.</exception>
    /// <exception cref="ControlClientException">The connection is unavailable or the service rejected the command.</exception>
    internal async Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken)
        where T : IControlResponse
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(arguments);
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var connection = _connectionTask is { } connectionTask ? await connectionTask : null;
            if (Volatile.Read(ref _state) != 1 || connection is null)
                throw new ControlClientException("connection-unavailable");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_commandTimeout);
            try
            {
                var response = await connection.InvokeCoreAsync<T>(method, arguments, deadline.Token);
                if (!response.Accepted)
                    throw new ControlClientException(response.Code);
                return response;
            }
            catch (HubException error)
            {
                throw new ControlClientException(error.Message, error);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ControlClientUnknownOutcomeException(error);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Runs a callback in connection order while the connection is active.
    /// </summary>
    /// <param name="callback">The callback to serialize with other notifications.</param>
    internal Task DispatchAsync(Func<Task> callback) => DispatchCoreAsync(callback);

    private async Task DispatchCoreAsync(Func<Task> callback)
    {
        if (Volatile.Read(ref _state) != 1)
            return;

        await _callbackGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _state) == 1)
                await callback();
        }
        finally
        {
            _callbackGate.Release();
        }
    }

    private async Task OnClosedAsync(Exception? error)
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return;
        var closed = _closed;
        if (closed is not null)
            await DispatchTerminalAsync(closed);
    }

    private async Task DispatchTerminalAsync(Func<Task> callback)
    {
        await _callbackGate.WaitAsync();
        try
        {
            await callback();
        }
        finally
        {
            _callbackGate.Release();
        }
    }

    /// <summary>
    /// Stops the owned hub connection and releases command and callback gates.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return;

        var connectionTask = Interlocked.Exchange(ref _connectionTask, null);
        if (connectionTask is not null)
        {
            var connection = await connectionTask;
            connection.Closed -= OnClosedAsync;
            try { await connection.StopAsync(); }
            finally { await connection.DisposeAsync(); }
        }
        _commandGate.Dispose();
        _callbackGate.Dispose();
    }
}
