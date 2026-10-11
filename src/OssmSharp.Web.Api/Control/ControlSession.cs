using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Models;
using static OssmSharp.Web.Contracts.ControlResponseCodes;

namespace OssmSharp.Web.Api.Control;

/// <summary>
/// Owns invitation redemption, saved guest admission, and control permission while delegating device behavior to the runtime.
/// </summary>
public sealed class ControlSession : IControlSession, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<string, IOssmDevice> _deviceFactory;
    private readonly IDeviceDiscovery _discovery;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ControlSession> _logger;
    private PreviewDimensions _previewDimensions = new();
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
    private byte[] _guestCredentialHash = [];
    private DateTimeOffset? _guestExpiresAt;
    private string? _guestConnectionId;
    private bool _guestControlAllowed;
    private bool _guestStopped;
    private long _permissionVersion;
    private Task? _pendingStop;
    private bool _stopRequired;
    private int _pendingConnections;
    private long _connectionVersion;
    private DeviceSettingsSnapshot? _lastSentSettings;
    private long _requestSequence;
    private long _lastSentSequence;
    private CancellationTokenSource? _workCancellation;
    private Task _workCleanup = Task.CompletedTask;
    private bool _resettingPosition;

    /// <summary>
    /// Initializes a control session that owns one caller-selected runtime device.
    /// </summary>
    /// <param name="deviceFactory">Creates the device retained for reconnects and disposed with this session.</param>
    /// <param name="discovery">Finds the first nearby device when no identifier is configured.</param>
    /// <param name="timeProvider">The time source used for connection timestamps and fixed guest-session expiry.</param>
    /// <param name="options">The validated control-service configuration; a configured device identifier bypasses discovery.</param>
    /// <param name="logger">Structured diagnostics for session-owned updates and failures.</param>
    public ControlSession(
        Func<string, IOssmDevice> deviceFactory,
        IDeviceDiscovery discovery,
        TimeProvider timeProvider,
        IOptions<ControlServiceOptions> options,
        ILogger<ControlSession> logger)
    {
        _deviceFactory = deviceFactory;
        _discovery = discovery;
        if (!string.IsNullOrWhiteSpace(options.Value.DeviceIdentifier))
        {
            _deviceIdentifier = options.Value.DeviceIdentifier;
            _device = deviceFactory(options.Value.DeviceIdentifier);
            _device.StateChanged += DeviceStateChanged;
        }

        _timeProvider = timeProvider;
        _logger = logger;
        _invitationToken = CapabilityTokens.NewToken();
        _invitationHash = CapabilityTokens.Hash(_invitationToken);
    }

    /// <inheritdoc />
    public async Task<UserSessionState> GetUserStateAsync(CancellationToken cancellationToken)
    {
        var device = await GetDeviceStateAsync(cancellationToken, includeUserDetails: true).ConfigureAwait(false);
        lock (_sync)
        {
            ExpireSessionLocked();
            var controlAllowed = HasActiveControlLocked();
            return new UserSessionState(
                new(_invitationToken),
                _guestExpiresAt is not null,
                controlAllowed,
                null,
                device)
            {
                Preview = _previewDimensions,
                GuestConnected = _guestConnectionId is not null,
                GuestSessionExpiresAt = _guestExpiresAt,
                InvitationConsumed = _guestExpiresAt is not null,
            };
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
            var joined = IsCurrentGuestLocked(connectionId);
            var allowed = joined && HasActiveControlLocked();
            return new GuestSessionState(joined, allowed, null, joined ? device : null, joined && allowed && _guestStopped && !_stopRequired && !_resettingPosition)
            {
                Preview = joined ? _previewDimensions : null,
                SessionExpiresAt = joined ? _guestExpiresAt : null,
            };
        }
    }

    /// <inheritdoc />
    public ControlResponse SetPreviewDimensions(string correlation, PreviewDimensions? dimensions)
    {
        if (!ValidCorrelation(correlation))
        {
            _logger.PreviewRejected(InvalidCorrelation);
            return Rejected(correlation, InvalidCorrelation);
        }
        if (dimensions is null || !double.IsFinite(dimensions.RailLengthCm) || !double.IsFinite(dimensions.AttachmentLengthCm)
            || dimensions.RailLengthCm is < PreviewDimensions.MinimumRailLengthCm or > PreviewDimensions.MaximumLengthCm
            || dimensions.AttachmentLengthCm is < 0 or > PreviewDimensions.MaximumLengthCm)
        {
            _logger.PreviewRejected(InvalidPreviewDimensions);
            return Rejected(correlation, InvalidPreviewDimensions);
        }
        lock (_sync)
        {
            if (_shuttingDown)
            {
                _logger.PreviewRejected(Cancelled);
                return Rejected(correlation, Cancelled);
            }
            _previewDimensions = dimensions;
        }
        _logger.PreviewUpdated(dimensions.RailLengthCm, dimensions.AttachmentLengthCm);
        return Accepted(correlation, ControlResponseCodes.PreviewUpdated);
    }

    /// <inheritdoc />
    public string? AuthenticateInvitation(string? token)
    {
        lock (_sync)
        {
            ExpireSessionLocked();
            return !_shuttingDown && _guestExpiresAt is null && CapabilityTokens.Matches(_invitationHash, token) ? _invitationId : null;
        }
    }

    /// <inheritdoc />
    public GuestRedemptionResponse RedeemInvitation(string? invitationId, GuestJoinRequest request)
    {
        if (!ValidCorrelation(request.Correlation))
        {
            return new(request.Correlation, InvalidCorrelation, false);
        }

        lock (_sync)
        {
            if (_device is not { IsConnected: true })
            {
                DiscardDisconnectedWorkLocked();
            }

            ExpireSessionLocked();
            if (_guestExpiresAt is not null || _stopRequired)
            {
                return new(request.Correlation, GuestAlreadyJoined, false);
            }

            if (_shuttingDown || !string.Equals(_invitationId, invitationId, StringComparison.Ordinal)
                || !CapabilityTokens.Matches(_invitationHash, request.InvitationToken))
            {
                return new(request.Correlation, InvalidInvitation, false);
            }

            var credential = CapabilityTokens.NewToken();
            _guestCredentialHash = CapabilityTokens.Hash(credential);
            _guestExpiresAt = _timeProvider.GetUtcNow().AddHours(24);
            _guestControlAllowed = false;
            _logger.GuestSessionChanged("admitted");
            return new(request.Correlation, InvitationRedeemed, true, credential, _guestExpiresAt);
        }
    }

    /// <inheritdoc />
    public string? AuthenticateGuestSession(string? token)
    {
        lock (_sync)
        {
            ExpireSessionLocked();
            return !_shuttingDown && _guestExpiresAt is not null && CapabilityTokens.Matches(_guestCredentialHash, token) ? _invitationId : null;
        }
    }

    /// <inheritdoc />
    public SavedGuestSession? GetSavedGuestSession(string? sessionId)
    {
        lock (_sync)
        {
            return IsSavedGuestLocked(sessionId)
                ? new(_guestConnectionId is not null, _guestExpiresAt!.Value, _guestConnectionId is null && !_stopRequired && !_resettingPosition)
                : null;
        }
    }

    /// <inheritdoc />
    public ControlResponse JoinGuest(string connectionId, string? sessionId, string correlation)
    {
        if (!ValidCorrelation(correlation))
            return Rejected(correlation, InvalidCorrelation);
        lock (_sync)
        {
            if (!IsSavedGuestLocked(sessionId))
                return Rejected(correlation, InvalidGuestSession);
            if (_guestConnectionId is not null)
                return Rejected(correlation, GuestAlreadyJoined);
            if (_stopRequired || _resettingPosition)
                return Rejected(correlation, DeviceNotReady);
            _guestConnectionId = connectionId;
            _logger.GuestSessionChanged("attached");
            return Accepted(correlation, Joined);
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
            ExpireSessionLocked();
            if (_guestExpiresAt is null)
            {
                return Rejected(correlation, GuestNotJoined);
            }
            if (_stopRequired || _resettingPosition || _device is not { IsConnected: true })
            {
                return Rejected(correlation, DeviceNotReady);
            }

            device = _device;
            guest = _invitationId;
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
                if (_stopRequired || _resettingPosition || !device.IsConnected || version != _permissionVersion || !IsSavedGuestLocked(guest))
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
            else if (_stopRequired || _resettingPosition)
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
            var status = result.Status switch
            {
                SetStatus.Completed => SettingsResponseStatus.Completed,
                SetStatus.Superseded => SettingsResponseStatus.Superseded,
                SetStatus.Cancelled => SettingsResponseStatus.Cancelled,
                _ => throw new UnreachableException(),
            };
            return new(
                request.Correlation,
                status,
                result.CommandsWritten);
        }
        catch (DeviceWriteException exception)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, exception.CommandsWritten, SettingsFailureCode(exception, DeviceWriteFailed));
        }
        catch (OperationCanceledException)
        {
            return new(request.Correlation, SettingsResponseStatus.Cancelled, 0);
        }
        catch (InvalidOperationException)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, 0, DeviceNotReady);
        }
        catch (IOException exception)
        {
            return new(request.Correlation, SettingsResponseStatus.Failed, 0, SettingsFailureCode(exception, DeviceIoFailed));
        }
    }

    /// <inheritdoc />
    public Task<ControlResponse> StopGuestAsync(
        string connectionId,
        string correlation) =>
        StopCoreAsync(connectionId, correlation, clearGuest: false);

    /// <inheritdoc />
    public Task<ControlResponse> ResetPositionGuestAsync(string connectionId, string correlation, CancellationToken cancellationToken) =>
        ResetPositionCoreAsync(connectionId, correlation, cancellationToken);

    /// <inheritdoc />
    public Task<ControlResponse> ResetPositionAsync(string correlation, CancellationToken cancellationToken) =>
        ResetPositionCoreAsync(null, correlation, cancellationToken);

    /// <inheritdoc />
    public Task<ControlResponse> LeaveGuestAsync(
        string connectionId,
        string correlation) =>
        StopCoreAsync(connectionId, correlation, clearGuest: true);

    /// <inheritdoc />
    public Task<ControlResponse> LeaveSavedGuestAsync(string? sessionId, string correlation)
    {
        if (!ValidCorrelation(correlation))
            return Task.FromResult(Rejected(correlation, InvalidCorrelation));
        Task stop;
        lock (_sync)
        {
            if (!IsSavedGuestLocked(sessionId))
                return Task.FromResult(Rejected(correlation, InvalidGuestSession));
            if (_guestConnectionId is not null)
                return Task.FromResult(Rejected(correlation, GuestAlreadyJoined));
            stop = RequestStopLocked(clearGuest: true);
        }
        return CompleteStopAsync(correlation, stop);
    }

    /// <inheritdoc />
    public async Task GuestDisconnectedAsync(string connectionId)
    {
        Task? stop = null;
        lock (_sync)
        {
            if (IsCurrentGuestLocked(connectionId))
            {
                _guestConnectionId = null;
                _lastSentSettings = null;
                CancelPendingWorkLocked();
                _logger.GuestSessionChanged("detached");
                stop = RequestStopLocked(clearGuest: false, preservePermission: true);
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
    public async Task<ControlResponse> RefreshInvitationAsync(string correlation)
    {
        if (!ValidCorrelation(correlation))
            return Rejected(correlation, InvalidCorrelation);
        Task? stop;
        lock (_sync)
        {
            var requiresStop = _guestExpiresAt is not null || _stopRequired;
            RotateInvitationLocked();
            stop = requiresStop ? RequestStopLocked(clearGuest: false) : null;
        }
        if (stop is not null)
        {
            var result = await CompleteStopAsync(correlation, stop).ConfigureAwait(false);
            if (!result.Accepted)
                return result;
        }
        return Accepted(correlation, InvitationRefreshed);
    }

    /// <inheritdoc />
    public Task<ControlResponse> ConnectAsync(string correlation, CancellationToken cancellationToken) =>
        DeviceCommandAsync(correlation, Connected, () => ConnectDeviceAsync(cancellationToken));

    /// <inheritdoc />
    public async Task ExpireGuestAsync()
    {
        Task? stop = null;
        lock (_sync)
        {
            ExpireSessionLocked();
            if (_device is not { IsConnected: true })
            {
                DiscardDisconnectedWorkLocked();
                return;
            }

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
        Task workCleanup;
        lock (_sync)
        {
            _shuttingDown = true;
            ClearGuestLocked();
            DiscardDisconnectedWorkLocked();
            workCleanup = _workCleanup;
            selectionStop = _selectionCancellation is { } cancellation && _selection is { IsCompleted: false } selection
                ? CancelSelectionAsync(cancellation, selection)
                : null;
        }

        try
        {
            if (selectionStop is not null)
            {
                await selectionStop.ConfigureAwait(false);
            }

            if (_device is { } device)
            {
                await device.DisconnectAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await workCleanup.ConfigureAwait(false);
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
                    device.StateChanged -= DeviceStateChanged;
                    await device.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _selectionCancellation?.Dispose();
                CryptographicOperations.ZeroMemory(_invitationHash);
                CryptographicOperations.ZeroMemory(_guestCredentialHash);
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
        catch (Exception exception) when (exception is IOException or AggregateException)
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
                    DiscardDisconnectedWorkLocked();
                }
                connect = device.ConnectAsync(cancellationToken);
            }

            await connect.ConfigureAwait(false);
            lock (_sync)
            {
                _connectedAt ??= _timeProvider.GetUtcNow();
                _deviceName = device.Name ?? _deviceName;
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
            _device = _deviceFactory(detected.Identifier);
            _device.StateChanged += DeviceStateChanged;
            return _device;
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.GuestStopCleanupIncomplete(exception is IOException ? DeviceIoFailed : DeviceNotConnected);
        }
    }

    private async Task StartStop()
    {
        if (_device is null && _selectionCancellation is { } cancellation && _selection is { IsCompleted: false } selection)
        {
            await CancelSelectionAsync(cancellation, selection).ConfigureAwait(false);
            return;
        }

        var device = _device ?? throw new InvalidOperationException("No device is connected.");
        var token = device.IsConnected ? GetWorkTokenLocked() : CancellationToken.None;
        await WaitForDeviceWorkAsync(device.EmergencyStopAsync(token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private Task RequestStopLocked(bool clearGuest, bool preservePermission = false)
    {
        if (clearGuest)
            RotateInvitationLocked();
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
        if (_device is not { IsConnected: true } && _pendingConnections == 0)
        {
            DiscardDisconnectedWorkLocked();
            return Task.FromException(new InvalidOperationException("No device is connected."));
        }

        _stopRequired = true;
        if (_pendingStop is { } pending)
        {
            return pending;
        }

        var version = _connectionVersion;
        var stop = StartStop();
        if (version == _connectionVersion)
        {
            _pendingStop = stop;
        }

        return stop;
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
                    if (_device is not { IsConnected: true })
                    {
                        DiscardDisconnectedWorkLocked();
                    }
                    else if (succeeded)
                    {
                        _stopRequired = false;
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

    private Task<ControlResponse> ResetPositionCoreAsync(string? connectionId, string correlation, CancellationToken cancellationToken)
    {
        if (!ValidCorrelation(correlation))
        {
            return Task.FromResult(Rejected(correlation, InvalidCorrelation));
        }

        lock (_sync)
        {
            if (connectionId is not null && !IsCurrentGuestLocked(connectionId))
            {
                return Task.FromResult(Rejected(correlation, GuestNotJoined));
            }
            if (connectionId is not null && !HasActiveControlLocked())
            {
                return Task.FromResult(Rejected(correlation, ControlNotAllowed));
            }
            if (_shuttingDown || _stopRequired || _resettingPosition || _device is not { IsConnected: true })
            {
                return Task.FromResult(Rejected(correlation, DeviceNotReady));
            }

            CancelPendingWorkLocked();
            _permissionVersion++;
            _resettingPosition = true;
            _guestStopped = _guestControlAllowed;
            _lastSentSettings = null;
            return DeviceCommandAsync(correlation, PositionReset, () => ResetPositionDeviceAsync(_device, cancellationToken));
        }
    }

    private async Task ResetPositionDeviceAsync(IOssmDevice device, CancellationToken cancellationToken)
    {
        var version = _permissionVersion;
        var connectionVersion = _connectionVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GetWorkTokenLocked());
        try
        {
            if (!device.IsArmed)
            {
                await device.ResetAsync(cancellation.Token).ConfigureAwait(false);
            }

            Task reset;
            lock (_sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_stopRequired || !device.IsConnected || version != _permissionVersion)
                {
                    throw new OperationCanceledException("Position reset was interrupted.");
                }

                reset = device.ResetPositionAsync(cancellation.Token);
            }

            await reset.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_sync)
            {
                if (connectionVersion == _connectionVersion)
                {
                    _resettingPosition = false;
                }
            }
        }
    }

    private string SettingsFailureCode(Exception exception, string fallback) =>
        exception.GetBaseException() is DeviceRequestException rejection
            ? rejection.Code switch
            {
                "invalid_value" => InvalidSettings,
                "guard_rejected" => DeviceNotReady,
                "lease_required" => DeviceControlLost,
                _ => fallback,
            }
            : _device?.IsConnected != true ? DeviceNotConnected : fallback;

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
                DiscardDisconnectedWorkLocked();
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
                includeUserDetails && connected && _pendingConnections == 0 && !_shuttingDown ? _lastSentSettings : null,
                _resettingPosition);
        }
    }

    private async Task<SetResult> SubmitDeviceAsync(string connectionId, MotionSettings settings, CancellationToken cancellationToken)
    {
        var device = _device ?? throw new InvalidOperationException("No device is connected.");
        var version = _permissionVersion;
        var connectionVersion = _connectionVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GetWorkTokenLocked());
        var token = cancellation.Token;
        if (_guestStopped && !device.IsArmed)
        {
            await WaitForDeviceWorkAsync(device.ResetAsync(token), token).ConfigureAwait(false);
        }

        Task<SetResult> request;
        long sequence;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (!device.IsConnected || !IsCurrentGuestLocked(connectionId) || !_guestControlAllowed || _stopRequired || _resettingPosition || version != _permissionVersion)
            {
                throw new InvalidOperationException("Guest control is no longer available.");
            }

            _guestStopped = false;
            sequence = ++_requestSequence;
            request = device.SetAsync(settings, token);
        }

        await WaitForDeviceWorkAsync(request, token).ConfigureAwait(false);
        var result = await request.ConfigureAwait(false);
        lock (_sync)
        {
            if (token.IsCancellationRequested || !device.IsConnected || connectionVersion != _connectionVersion
                || (result.Status == SetStatus.Completed
                    && (!IsCurrentGuestLocked(connectionId) || version != _permissionVersion)))
            {
                return new(SetStatus.Cancelled, result.CommandsWritten);
            }

            if (result.Status == SetStatus.Completed && sequence > _lastSentSequence)
            {
                _lastSentSequence = sequence;
                _lastSentSettings = new((int)settings.Pattern, settings.Speed, settings.Stroke, settings.Depth, settings.Sensation);
            }
        }

        return result;
    }

    private bool HasActiveControlLocked()
    {
        if (_device is not { IsConnected: true })
        {
            DiscardDisconnectedWorkLocked();
        }

        return _guestControlAllowed;
    }

    private void DiscardDisconnectedWorkLocked()
    {
        CancelPendingWorkLocked();
        _resettingPosition = false;
        _connectionVersion++;
        _permissionVersion++;
        _connectedAt = null;
        _guestControlAllowed = false;
        _guestStopped = false;
        _pendingStop = null;
        _stopRequired = false;
        _lastSentSettings = null;
    }

    private void CancelPendingWorkLocked()
    {
        if (_workCancellation is { } cancellation)
        {
            _workCancellation = null;
            _workCleanup = Task.WhenAll(_workCleanup, CancelWorkAsync(cancellation));
        }
    }

    private void DeviceStateChanged(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_device is not { IsConnected: true })
            {
                DiscardDisconnectedWorkLocked();
            }
        }
    }

    private CancellationToken GetWorkTokenLocked() =>
        (_workCancellation ??= new CancellationTokenSource()).Token;

    private static async Task CancelWorkAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static async Task WaitForDeviceWorkAsync(Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = operation.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private bool IsCurrentGuestLocked(string connectionId)
    {
        ExpireSessionLocked();
        return !_shuttingDown && _guestExpiresAt is not null && string.Equals(_guestConnectionId, connectionId, StringComparison.Ordinal);
    }

    private bool IsSavedGuestLocked(string? sessionId)
    {
        ExpireSessionLocked();
        return !_shuttingDown && _guestExpiresAt is not null && string.Equals(_invitationId, sessionId, StringComparison.Ordinal);
    }

    private void ExpireSessionLocked()
    {
        if (_guestExpiresAt is { } expiry && _timeProvider.GetUtcNow() >= expiry)
        {
            RotateInvitationLocked();
            _logger.GuestSessionChanged("expired");
            if (_device is { IsConnected: true })
                _ = ObserveStopAsync(RequestStopLocked(clearGuest: false));
        }
    }

    private void ClearGuestLocked()
    {
        _guestConnectionId = null;
        _guestControlAllowed = false;
        _guestStopped = false;
        _guestExpiresAt = null;
        CryptographicOperations.ZeroMemory(_guestCredentialHash);
        _guestCredentialHash = [];
        CancelPendingWorkLocked();
        _permissionVersion++;
        _lastSentSettings = null;
    }

    private void RotateInvitationLocked()
    {
        ClearGuestLocked();
        CryptographicOperations.ZeroMemory(_invitationHash);
        _invitationToken = CapabilityTokens.NewToken();
        _invitationHash = CapabilityTokens.Hash(_invitationToken);
        _invitationId = Guid.NewGuid().ToString("N");
        _logger.GuestSessionChanged("invitation-replaced");
    }

}
