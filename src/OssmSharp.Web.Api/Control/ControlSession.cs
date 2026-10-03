using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Models;
using static OssmSharp.Web.Contracts.ControlResponseCodes;

namespace OssmSharp.Web.Api.Control;

/// <summary>
/// Owns UI invitation and guest-permission state while delegating device behavior to the runtime.
/// </summary>
public sealed class ControlSession : IControlSession, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<string, IOssmDevice> _deviceFactory;
    private readonly IDeviceDiscovery _discovery;
    private readonly TimeProvider _timeProvider;
    private IOssmDevice? _device;
    private string? _deviceIdentifier;
    private string? _deviceName;
    private DateTimeOffset? _connectedAt;
    private Task<IOssmDevice>? _selection;
    private CancellationTokenSource? _selectionCancellation;
    private bool _shuttingDown;
    private string _invitationToken;
    private byte[] _invitationHash;
    private string _invitationId = Guid.NewGuid().ToString("N");
    private string? _guestConnectionId;
    private bool _guestControlAllowed;
    private bool _guestStopped;
    private long _permissionVersion;
    private Task? _pendingStop;
    private bool _clearGuestAfterStop;
    private bool _stopRequired;
    private int _pendingConnections;
    private DeviceSettingsSnapshot? _lastSentSettings;
    private long _requestSequence;
    private long _lastSentSequence;

    /// <summary>
    /// Initializes a control session that owns one caller-selected runtime device.
    /// </summary>
    /// <param name="deviceFactory">Creates the session-owned runtime device for a selected identifier.</param>
    /// <param name="discovery">Finds the first nearby device when no identifier is configured.</param>
    /// <param name="timeProvider">The time source used for connection timestamps.</param>
    /// <param name="options">The validated control-service configuration.</param>
    /// <remarks>
    /// A configured identifier bypasses discovery. Otherwise, Connect discovers before constructing the device.
    /// The selected device is retained for reconnects and asynchronously disposed with this session.
    /// </remarks>
    public ControlSession(
        Func<string, IOssmDevice> deviceFactory,
        IDeviceDiscovery discovery,
        TimeProvider timeProvider,
        IOptions<ControlServiceOptions> options)
    {
        _deviceFactory = deviceFactory;
        _discovery = discovery;
        if (!string.IsNullOrWhiteSpace(options.Value.DeviceIdentifier))
        {
            _deviceIdentifier = options.Value.DeviceIdentifier;
            _device = deviceFactory(options.Value.DeviceIdentifier);
        }

        _timeProvider = timeProvider;
        _invitationToken = CapabilityTokens.NewToken();
        _invitationHash = CapabilityTokens.Hash(_invitationToken);
    }

    /// <inheritdoc />
    public async Task<UserSessionState> GetUserStateAsync(CancellationToken cancellationToken)
    {
        var device = await GetDeviceStateAsync(cancellationToken, includeUserDetails: true).ConfigureAwait(false);
        lock (_sync)
        {
            var controlAllowed = HasActiveControlLocked();
            return new(
                new(_invitationToken),
                _guestConnectionId is not null,
                controlAllowed,
                null,
                device);
        }
    }

    /// <inheritdoc />
    public async Task<GuestSessionState> GetGuestStateAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExpireGuestAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
        }
        var device = await GetDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var joined = string.Equals(_guestConnectionId, connectionId, StringComparison.Ordinal);
            var allowed = joined && HasActiveControlLocked();
            return new(joined, allowed, null, joined ? device : null, joined && allowed && _guestStopped && !_stopRequired);
        }
    }

    /// <inheritdoc />
    public string? AuthenticateInvitation(string? token)
    {
        lock (_sync)
            return !_shuttingDown && CapabilityTokens.Matches(_invitationHash, token) ? _invitationId : null;
    }

    /// <inheritdoc />
    public ControlResponse JoinGuest(string connectionId, string? invitationId, GuestJoinRequest request)
    {
        if (!ValidCorrelation(request.Correlation))
        {
            return Rejected(request.Correlation, InvalidCorrelation);
        }

        lock (_sync)
        {
            if (_guestConnectionId is not null || _stopRequired)
            {
                return Rejected(request.Correlation, GuestAlreadyJoined);
            }

            if (!string.Equals(_invitationId, invitationId, StringComparison.Ordinal)
                || !CapabilityTokens.Matches(_invitationHash, request.InvitationToken))
            {
                return Rejected(request.Correlation, InvalidInvitation);
            }

            _guestConnectionId = connectionId;
            _guestControlAllowed = false;
            RotateInvitationLocked();
            return Accepted(request.Correlation, Joined);
        }
    }

    /// <inheritdoc />
    public async Task<ControlResponse> AllowGuestAsync(string correlation, CancellationToken cancellationToken)
    {
        if (!ValidCorrelation(correlation))
        {
            return Rejected(correlation, InvalidCorrelation);
        }

        IOssmDevice device;
        string guest;
        long version;
        lock (_sync)
        {
            if (_guestConnectionId is null)
            {
                return Rejected(correlation, GuestNotJoined);
            }
            if (_stopRequired || _device is null)
            {
                return Rejected(correlation, DeviceNotReady);
            }

            device = _device;
            guest = _guestConnectionId;
            version = _permissionVersion;
        }

        return await DeviceCommandAsync(correlation, ControlAllowed, async () =>
        {
            if (!device.IsArmed)
            {
                await device.ResetAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_stopRequired || version != _permissionVersion || !IsCurrentGuestLocked(guest))
                {
                    throw new InvalidOperationException("Control permission was interrupted by Stop or a session change.");
                }

                _guestControlAllowed = true;
                _guestStopped = false;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ControlResponse> RenewGuestAsync(string connectionId, string correlation)
    {
        if (!ValidCorrelation(correlation))
        {
            return Rejected(correlation, InvalidCorrelation);
        }

        Task? stop = null;
        ControlResponse response;
        lock (_sync)
        {
            if (!IsCurrentGuestLocked(connectionId))
            {
                return Rejected(correlation, GuestNotJoined);
            }

            if (!HasActiveControlLocked())
            {
                if (_stopRequired)
                {
                    stop = RequestAutomaticStopLocked();
                }

                response = Rejected(correlation, ControlNotAllowed);
            }
            else
            {
                response = Accepted(correlation, ControlRenewed);
            }
        }

        if (stop is not null)
        {
            await ObserveStopAsync(stop).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc />
    public async Task<SettingsResponse> SubmitAsync(
        string connectionId,
        SettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!ValidCorrelation(request.Correlation))
        {
            return RejectedSettings(request.Correlation, InvalidCorrelation);
        }

        MotionSettings settings;
        try
        {
            settings = new((StrokePattern)request.Pattern, request.Speed, request.Stroke, request.Depth, request.Sensation);
        }
        catch (ArgumentOutOfRangeException)
        {
            return RejectedSettings(request.Correlation, InvalidSettings);
        }

        Task<SetResult>? operation = null;
        string? rejection = null;
        lock (_sync)
        {
            if (!IsCurrentGuestLocked(connectionId))
            {
                rejection = GuestNotJoined;
            }
            else if (!HasActiveControlLocked())
            {
                rejection = ControlNotAllowed;
            }
            else if (_stopRequired)
            {
                rejection = DeviceNotReady;
            }
            else
            {
                operation = SubmitDeviceAsync(connectionId, settings, cancellationToken);
            }
        }

        if (rejection is not null)
        {
            return RejectedSettings(request.Correlation, rejection);
        }

        try
        {
            var result = await operation!.ConfigureAwait(false);
            return new(
                request.Correlation,
                result.Status switch
                {
                    SetStatus.Completed => SettingsResponseStatus.Completed,
                    SetStatus.Superseded => SettingsResponseStatus.Superseded,
                    SetStatus.Cancelled => SettingsResponseStatus.Cancelled,
                    _ => throw new UnreachableException()
                },
                result.CommandsWritten);
        }
        catch (DeviceWriteException exception)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, exception.CommandsWritten, DeviceWriteFailed);
        }
        catch (OperationCanceledException)
        {
            return new(request.Correlation, SettingsResponseStatus.Cancelled, 0);
        }
        catch (InvalidOperationException)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, 0, DeviceNotReady);
        }
        catch (IOException)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, 0, DeviceIoFailed);
        }
    }

    /// <inheritdoc />
    public Task<ControlResponse> StopGuestAsync(
        string connectionId,
        string correlation) =>
        StopCoreAsync(connectionId, correlation, clearGuest: false);

    /// <inheritdoc />
    public Task<ControlResponse> LeaveGuestAsync(
        string connectionId,
        string correlation) =>
        StopCoreAsync(connectionId, correlation, clearGuest: true);

    /// <inheritdoc />
    public async Task GuestDisconnectedAsync(string connectionId)
    {
        Task? stop = null;
        lock (_sync)
        {
            if (IsCurrentGuestLocked(connectionId))
            {
                stop = RequestStopLocked(clearGuest: true);
            }
        }

        if (stop is not null)
        {
            await ObserveStopAsync(stop).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<ControlResponse> StopAsync(string correlation) =>
        UserStopCoreAsync(correlation, clearGuest: false);

    /// <inheritdoc />
    public Task<ControlResponse> EndGuestAsync(string correlation) =>
        UserStopCoreAsync(correlation, clearGuest: true);

    /// <inheritdoc />
    public Task<ControlResponse> ConnectAsync(string correlation, CancellationToken cancellationToken) =>
        DeviceCommandAsync(correlation, Connected, () => ConnectDeviceAsync(cancellationToken));

    /// <inheritdoc />
    public async Task ExpireGuestAsync()
    {
        Task? stop = null;
        lock (_sync)
        {
            if (_stopRequired)
            {
                stop = RequestAutomaticStopLocked();
            }
        }

        if (stop is not null)
        {
            await FinishStopAsync(stop).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ShutdownAsync()
    {
        Task? selectionStop;
        lock (_sync)
        {
            _shuttingDown = true;
            selectionStop = _selectionCancellation is { } cancellation && _selection is { IsCompleted: false } selection
                ? CancelSelectionAsync(cancellation, selection)
                : null;
        }

        if (selectionStop is not null)
        {
            await selectionStop.ConfigureAwait(false);
        }

        if (_device is { } device)
        {
            await device.DisconnectAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels discovery, disposes the session-owned device, and clears invitation capability material.
    /// </summary>
    /// <returns>Completion of device cleanup and invitation clearing.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_device is { } device)
                {
                    await device.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _selectionCancellation?.Dispose();
                CryptographicOperations.ZeroMemory(_invitationHash);
                _invitationHash = [];
                _invitationToken = string.Empty;
            }
        }
    }

    private static async Task<ControlResponse> DeviceCommandAsync(
        string correlation,
        string successCode,
        Func<Task> operation)
    {
        if (!ValidCorrelation(correlation))
        {
            return Rejected(correlation, InvalidCorrelation);
        }

        try
        {
            await operation().ConfigureAwait(false);
            return Accepted(correlation, successCode);
        }
        catch (OperationCanceledException)
        {
            return Rejected(correlation, Cancelled);
        }
        catch (InvalidOperationException)
        {
            return Rejected(correlation, DeviceNotReady);
        }
        catch (IOException)
        {
            return Rejected(correlation, DeviceIoFailed);
        }
    }

    private static async Task CancelSelectionAsync(CancellationTokenSource cancellation, Task selection)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await selection.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static bool ValidCorrelation(string? correlation) =>
        correlation is { Length: > 0 and <= 64 } && !correlation.Any(char.IsControl);

    private static ControlResponse Accepted(string correlation, string code) => new(correlation, code, true);

    private static ControlResponse Rejected(string? correlation, string code) => new(correlation ?? string.Empty, code, false);

    private static SettingsResponse RejectedSettings(string? correlation, string code) =>
        new(correlation ?? string.Empty, SettingsResponseStatus.Rejected, 0, code);

    private async Task ConnectDeviceAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _pendingConnections++;
        }

        try
        {
            var device = await SelectDeviceAsync(cancellationToken).ConfigureAwait(false);
            Task connect;
            lock (_sync)
            {
                if (_shuttingDown)
                {
                    throw new OperationCanceledException("The control session is shutting down.");
                }

                if (!device.IsConnected)
                {
                    _connectedAt = null;
                }
                connect = device.ConnectAsync(cancellationToken);
            }

            await connect.ConfigureAwait(false);
            Task? stop;
            lock (_sync)
            {
                _connectedAt ??= _timeProvider.GetUtcNow();
                _deviceName = device.Name ?? _deviceName;
                stop = _stopRequired ? RequestStopLocked(clearGuest: false, preservePermission: _guestStopped && _guestControlAllowed) : null;
            }

            if (stop is not null)
            {
                await FinishStopAsync(stop).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync)
            {
                _pendingConnections--;
            }
        }
    }

    private Task<IOssmDevice> SelectDeviceAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_shuttingDown)
            {
                throw new OperationCanceledException("The control session is shutting down.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_device is { } device)
            {
                return Task.FromResult(device);
            }

            if (_selection is { IsCompleted: false })
            {
                return _selection.WaitAsync(cancellationToken);
            }

            _selectionCancellation?.Dispose();
            _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _selection = DiscoverDeviceAsync(_selectionCancellation);
        }
    }

    private async Task<IOssmDevice> DiscoverDeviceAsync(CancellationTokenSource cancellation)
    {
        var detected = await _discovery.FindFirstAsync(cancellation.Token).ConfigureAwait(false);
        lock (_sync)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (detected is null)
            {
                throw new InvalidOperationException("No device was discovered. Check that the device is powered on and advertising, then connect again.");
            }

            _deviceIdentifier = detected.Identifier;
            _deviceName = detected.Name;
            return _device = _deviceFactory(detected.Identifier);
        }
    }

    private async Task<ControlResponse> CompleteStopAsync(string correlation, Task stop)
    {
        try
        {
            await FinishStopAsync(stop).ConfigureAwait(false);
            return Accepted(correlation, Stopped);
        }
        catch (OperationCanceledException)
        {
            return Rejected(correlation, Cancelled);
        }
        catch (InvalidOperationException)
        {
            return Rejected(correlation, DeviceNotConnected);
        }
        catch (IOException)
        {
            return Rejected(correlation, DeviceIoFailed);
        }
    }

    private async Task ObserveStopAsync(Task stop)
    {
        try
        {
            await FinishStopAsync(stop).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
        }
    }

    private Task StartStop()
    {
        try
        {
            if (_device is null && _selectionCancellation is { } cancellation && _selection is { IsCompleted: false } selection)
            {
                return CancelSelectionAsync(cancellation, selection);
            }

            return (_device ?? throw new InvalidOperationException("No device is connected."))
                .EmergencyStopAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            return Task.FromException(exception);
        }
    }

    private Task RequestStopLocked(bool clearGuest, bool preservePermission = false)
    {
        _permissionVersion++;
        if (!preservePermission)
        {
            _guestControlAllowed = false;
            _guestStopped = false;
        }
        else
        {
            _guestStopped = _guestControlAllowed;
        }
        _stopRequired = true;
        _clearGuestAfterStop |= clearGuest;
        return _pendingStop ??= StartStop();
    }

    private Task? RequestAutomaticStopLocked() =>
        _stopRequired && _pendingConnections > 0 ? _pendingStop
            : RequestStopLocked(clearGuest: false, preservePermission: _guestStopped && _guestControlAllowed);

    private async Task FinishStopAsync(Task stop)
    {
        var succeeded = false;
        try
        {
            await stop.ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pendingStop, stop))
                {
                    _pendingStop = null;
                    if (succeeded)
                    {
                        _stopRequired = false;
                        if (_clearGuestAfterStop)
                        {
                            ClearGuestLocked();
                        }
                        _clearGuestAfterStop = false;
                    }
                }
            }
        }
    }

    private async Task<ControlResponse> StopCoreAsync(
        string connectionId,
        string correlation,
        bool clearGuest)
    {
        if (!ValidCorrelation(correlation))
        {
            return Rejected(correlation, InvalidCorrelation);
        }

        Task stop;
        lock (_sync)
        {
            if (!IsCurrentGuestLocked(connectionId))
            {
                return Rejected(correlation, GuestNotJoined);
            }

            stop = RequestStopLocked(clearGuest, preservePermission: !clearGuest);
        }

        return await CompleteStopAsync(correlation, stop).ConfigureAwait(false);
    }

    private async Task<ControlResponse> UserStopCoreAsync(
        string correlation,
        bool clearGuest)
    {
        if (!ValidCorrelation(correlation))
        {
            return Rejected(correlation, InvalidCorrelation);
        }

        Task stop;
        lock (_sync)
        {
            stop = RequestStopLocked(clearGuest);
        }

        return await CompleteStopAsync(correlation, stop).ConfigureAwait(false);
    }

    private async Task<DeviceStateSnapshot> GetDeviceStateAsync(CancellationToken cancellationToken, bool includeUserDetails = false)
    {
        IOssmDevice? device;
        lock (_sync)
        {
            device = _device;
        }

        if (device is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return new(null, null, null, null, null, false, false, IsConnecting: _pendingConnections > 0);
            }
        }

        var state = await device.GetStateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var connected = device.IsConnected;
            if (!connected)
            {
                _connectedAt = null;
            }
            return new(
                state is null ? null : (int)state.Pattern,
                state?.Speed,
                state?.Stroke,
                state?.Depth,
                state?.Sensation,
                device.IsArmed,
                device.ConnectionFailure is not null,
                connected,
                _pendingConnections > 0 && !connected,
                includeUserDetails ? device.Name ?? _deviceName : null,
                includeUserDetails ? _deviceIdentifier : null,
                includeUserDetails ? _connectedAt : null,
                includeUserDetails && connected && _pendingConnections == 0 && !_shuttingDown ? _lastSentSettings : null);
        }
    }

    private async Task<SetResult> SubmitDeviceAsync(string connectionId, MotionSettings settings, CancellationToken cancellationToken)
    {
        var device = _device ?? throw new InvalidOperationException("No device is connected.");
        var version = _permissionVersion;
        if (_guestStopped && !device.IsArmed)
        {
            await device.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        Task<SetResult> request;
        long sequence;
        lock (_sync)
        {
            if (!IsCurrentGuestLocked(connectionId) || !_guestControlAllowed || _stopRequired || version != _permissionVersion)
            {
                throw new InvalidOperationException("Guest control is no longer available.");
            }

            _guestStopped = false;
            sequence = ++_requestSequence;
            request = device.SetAsync(settings, cancellationToken);
        }

        var result = await request.ConfigureAwait(false);
        lock (_sync)
        {
            if (result.Status == SetStatus.Completed && sequence > _lastSentSequence)
            {
                _lastSentSequence = sequence;
                _lastSentSettings = new((int)settings.Pattern, settings.Speed, settings.Stroke, settings.Depth, settings.Sensation);
            }
        }

        return result;
    }

    private bool HasActiveControlLocked() => _guestControlAllowed;

    private bool IsCurrentGuestLocked(string connectionId) =>
        string.Equals(_guestConnectionId, connectionId, StringComparison.Ordinal);

    private void ClearGuestLocked()
    {
        _guestConnectionId = null;
        _guestControlAllowed = false;
        _guestStopped = false;
    }

    private void RotateInvitationLocked()
    {
        CryptographicOperations.ZeroMemory(_invitationHash);
        _invitationToken = CapabilityTokens.NewToken();
        _invitationHash = CapabilityTokens.Hash(_invitationToken);
        _invitationId = Guid.NewGuid().ToString("N");
    }

}
