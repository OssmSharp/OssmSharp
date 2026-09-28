using System.ComponentModel.DataAnnotations;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.Options;
using OssmSharp.Device.Runtime;
using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock;

/// <summary>
/// Represents an OSSM device running the supported stock firmware.
/// </summary>
public sealed class StockOssmDevice : DeviceBase
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly TimeProvider _timeProvider;
    private readonly IDeviceTransport _transport;
    private readonly DeviceIdentifier _selected;
    private readonly IStockQualificationResolver _qualificationResolver;
    private IDeviceConnection? _activeConnection;
    private StockQualifiedMapping? _qualifiedMapping;
    private bool _knobLimitingVerified;
    private Exception? _connectionCleanupFailure;

    internal StockOssmDevice(
        IOptions<DeviceRuntimeOptions> options,
        TimeProvider timeProvider,
        ILogger<StockOssmDevice> logger,
        DeviceIdentifier selected,
        IDeviceTransport transport,
        IStockQualificationResolver? qualificationResolver = null)
        : base(
            options?.Value ?? throw new ArgumentNullException(nameof(options)),
            timeProvider,
            logger)
    {
        _timeProvider = timeProvider;
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _qualificationResolver = qualificationResolver ?? new UnqualifiedStockMotion();
    }

    /// <summary>
    /// Checks whether a reported effective speed is consistent with the requested speed and prior knob limit.
    /// </summary>
    /// <param name="previousRequestedSpeed">The preceding requested percentage, if known.</param>
    /// <param name="previousStateSpeed">The preceding reported effective percentage, when available.</param>
    /// <param name="requestedSpeed">The current requested percentage.</param>
    /// <param name="stateSpeed">The current reported effective percentage.</param>
    /// <returns>Whether the reported speed can be attributed to this write, allowing firmware truncation.</returns>
    internal static bool IsCurrentStateCongruent(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed)
    {
        if (requestedSpeed == 0)
            return stateSpeed == 0;
        if (stateSpeed < 0 || stateSpeed > requestedSpeed)
            return false;
        if (previousRequestedSpeed is not > 0 || previousStateSpeed is null)
            return true;
        if (previousStateSpeed < 0 || previousStateSpeed > previousRequestedSpeed)
            return false;
        if (previousStateSpeed == previousRequestedSpeed || stateSpeed == requestedSpeed)
            return previousStateSpeed == previousRequestedSpeed && stateSpeed == requestedSpeed;

        return (long)previousStateSpeed.Value * requestedSpeed <
               (long)(stateSpeed + 1) * previousRequestedSpeed.Value &&
               (long)stateSpeed * previousRequestedSpeed.Value <
               (long)(previousStateSpeed.Value + 1) * requestedSpeed;
    }

    /// <inheritdoc />
    /// <remarks>Requires a qualified mapping for the connection and a verified speed-knob limit.</remarks>
    protected override void ValidateMotion(MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Volatile.Read(ref _activeConnection) is null)
            throw new InvalidOperationException("The stock BLE connection is not ready for motion.");
        if (!Volatile.Read(ref _knobLimitingVerified))
            throw new InvalidOperationException("Stock motion requires a verified speed-knob limit.");
        RequireMapping().ValidateAndMap(settings);
    }

    /// <inheritdoc />
    /// <remarks>Enables the physical speed-knob limit and verifies its readback.</remarks>
    protected override async Task PrepareAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        var connection = Volatile.Read(ref _activeConnection)
            ?? throw new IOException("The stock BLE connection is not ready for configuration.");
        Volatile.Write(ref _knobLimitingVerified, false);
        await connection.EnableKnobLimitingAsync(requestCancellationToken, shutdownToken)
            .ConfigureAwait(false);
        requestCancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(connection, Volatile.Read(ref _activeConnection)))
            throw new IOException("The stock BLE connection ended during knob-limit verification.");
        Volatile.Write(ref _knobLimitingVerified, true);
    }

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var connection = await _transport.ConnectAsync(
                _selected, StockFirmwareProfile.Service, timeout.Token).ConfigureAwait(false);
            try
            {
                var profile = await connection.ReadProfileAsync(timeout.Token).ConfigureAwait(false);
                if (!string.Equals(profile.PeripheralId, _selected.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The connected peripheral differs from the selected locator.");
                }

                var device = StockFirmwareProfile.Verify(
                    profile.PeripheralId, profile.Service, profile.Characteristics,
                    profile.Identity, profile.Patterns) with { Identifier = _selected };
                var identity = PairingIdentity.Parse(profile.Identity);
                var mapping = _qualificationResolver.Resolve(device, identity);
                mapping?.VerifyConnection(device, identity);
                if (profile.Notifications.Completion.IsCompleted)
                {
                    throw new IOException("The stock BLE connection ended during identification.");
                }

                Volatile.Write(ref _qualifiedMapping, mapping);
                Volatile.Write(ref _activeConnection, connection);
                PublishConnected(device);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);

                await foreach (var payload in profile.Notifications.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (payload.Length is 0 or > 4096)
                    {
                        throw new InvalidDataException("The stock state notification exceeds its bounds.");
                    }

                    var state = StateResponse.Parse(
                        StrictUtf8.GetString(payload), _timeProvider.GetUtcNow().UtcDateTime);
                    PublishState(state.ToDeviceState(), null);
                }
            }
            finally
            {
                Volatile.Write(ref _knobLimitingVerified, false);
                Volatile.Write(ref _qualifiedMapping, null);
                Volatile.Write(ref _activeConnection, null);
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _connectionCleanupFailure = exception;
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Stock BLE discovery or identification timed out.");
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException))
        {
            throw new IOException("Stock BLE operation failed.", exception);
        }
    }

    /// <inheritdoc />
    protected override async Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        var connection = Volatile.Read(ref _activeConnection)
            ?? throw new IOException("The stock BLE connection is not ready for settings writes.");
        var commands = Request.FromValidatedMotion(
            RequireMapping().ValidateAndMap(motionSettings),
            LatestState?.MotionSettings.Depth);

        // An emergency stop cancels the request token, so the rest of a sequence is not sent after it.
        foreach (var command in commands)
        {
            requestCancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(connection, Volatile.Read(ref _activeConnection)))
                throw new IOException("The stock BLE connection ended during settings dispatch.");
            MarkWriteIssued();
            await connection.WriteCommandAsync(command, shutdownToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task DispatchSafetyStopAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        var connection = Volatile.Read(ref _activeConnection)
            ?? throw new IOException("The stock BLE connection is not ready for a safety Stop.");
        requestCancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(connection, Volatile.Read(ref _activeConnection)))
            throw new IOException("The stock BLE connection ended before safety Stop issuance.");
        MarkWriteIssued();
        await connection.WriteCommandAsync(Request.ZeroSpeed(), shutdownToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        await base.DisposeCoreAsync().ConfigureAwait(false);
        if (_connectionCleanupFailure is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    protected override bool IsExpectedState(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed)
        => IsCurrentStateCongruent(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);

    private StockQualifiedMapping RequireMapping() =>
        Volatile.Read(ref _qualifiedMapping)
        ?? throw new InvalidOperationException("Stock motion lacks a qualified mapping for this connection.");
}
