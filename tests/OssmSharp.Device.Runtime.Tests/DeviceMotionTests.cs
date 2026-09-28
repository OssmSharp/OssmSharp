using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OneOf;

namespace OssmSharp.Device.Runtime.Tests;

/// <summary>
/// Verifies the command queue, stopped setting changes, emergency stop and re-initialization.
/// </summary>
public sealed class DeviceMotionTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly TestDevice _device;
    private readonly Channel<(MotionSettings Settings, bool SafetyStop)> _writes =
        Channel.CreateUnbounded<(MotionSettings Settings, bool SafetyStop)>();
    private readonly Channel<Exception> _faults = Channel.CreateUnbounded<Exception>();

    public DeviceMotionTests()
    {
        _device = new TestDevice(_clock, NullLogger<TestDevice>.Instance)
        {
            BeforeDispatch = (settings, safetyStop) => _writes.Writer.TryWrite((settings, safetyStop)),
        };
        _device.DeviceStateChanged += change =>
        {
            if (change.IsT1)
                _faults.Writer.TryWrite(change.AsT1);
        };
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SetRequiresInitialization()
    {
        ConnectStopped();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _device.SetAsync(Stopped() with { Speed = 10 }, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _device.SetAsync(Stopped(), Token));

        Assert.False(_device.IsReady);
        Assert.Equal(0, _device.DispatchCount);
    }

    [Fact]
    public async Task InitializationWritesZeroAndMakesTheDeviceReady()
    {
        ConnectStopped();

        await _device.InitializeAsync(Token);

        Assert.True(_device.IsReady);
        Assert.Equal(1, _device.DispatchCount);
        Assert.Equal(0, (await _writes.Reader.ReadAsync(Token)).Settings.Speed);
    }

    [Fact]
    public async Task InitializationRequiresAStoppedLastKnownState()
    {
        _device.Connect();
        _device.ReportState(Stopped() with { Speed = 5 }, _clock.GetUtcNow().UtcDateTime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _device.InitializeAsync(Token));

        Assert.Equal(0, _device.DispatchCount);
    }

    [Fact]
    public async Task InitializationAcceptsAnOldStoppedLastKnownState()
    {
        // Stock firmware reports only changes, so an unchanged stopped device goes quiet.
        ConnectStopped();
        _clock.Advance(TimeSpan.FromSeconds(10));

        await _device.InitializeAsync(Token);

        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task CommandsRunOneAtATimeInOrder()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;

        var first = _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        Assert.Equal(10, (await _writes.Reader.ReadAsync(Token)).Settings.Speed);
        var second = _device.SetAsync(Stopped() with { Speed = 12 }, Token);
        Assert.False(_writes.Reader.TryRead(out _));
        _device.CompleteDispatch();
        await first;
        await second;

        Assert.Equal(12, (await _writes.Reader.ReadAsync(Token)).Settings.Speed);
    }

    [Fact]
    public async Task NewCommandSupersedesPendingCommandsWithoutOverlappingIssuedIo()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;
        var issued = _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _writes.Reader.ReadAsync(Token);
        var superseded = _device.SetAsync(Stopped() with { Speed = 20 }, Token);

        var latest = _device.SetAsync(Stopped() with { Speed = 30 }, Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            superseded.WaitAsync(TimeSpan.FromSeconds(2), Token));
        Assert.False(issued.IsCompleted);
        Assert.False(latest.IsCompleted);
        Assert.False(_writes.Reader.TryRead(out _));
        _device.CompleteDispatch();
        await issued;
        await latest;
        Assert.Equal(30, (await _writes.Reader.ReadAsync(Token)).Settings.Speed);
        Assert.False(_writes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CancellingStopWaitDoesNotCancelTheOwnedStop()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;
        var issued = _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _writes.Reader.ReadAsync(Token);
        using var cancellation = new CancellationTokenSource();

        var stop = _device.EmergencyStopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.False(_device.IsReady);
        _device.CompleteDispatch();
        await issued;
        await _device.EmergencyStopAsync(Token);

        Assert.True((await _writes.Reader.ReadAsync(Token)).SafetyStop);
    }

    [Fact]
    public async Task SetWritesTheFullRequestedSettingsEveryTime()
    {
        await ConnectReadyAsync(Stopped() with { Stroke = 30, Depth = 50 });
        var requested = Stopped() with { Speed = 10, Stroke = 20, Depth = 30 };

        await _device.SetAsync(requested, Token);
        await _device.SetAsync(requested, Token);

        Assert.Equal(requested, (await _writes.Reader.ReadAsync(Token)).Settings);
        Assert.Equal(requested, (await _writes.Reader.ReadAsync(Token)).Settings);
        Assert.False(_writes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ProviderRejectsUnsupportedSettingsBeforeAnyWrite()
    {
        await ConnectReadyAsync(Stopped() with { Stroke = 30, Depth = 50 });
        _device.MotionValidation = settings =>
        {
            if (settings.Depth == 30)
                throw new InvalidOperationException("The envelope is not supported.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _device.SetAsync(Stopped() with { Speed = 10, Stroke = 20, Depth = 30 }, Token));

        Assert.Equal(1, _device.DispatchCount); // Initialization only.
        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task EmergencyStopDrainsTheQueueWaitsForTheIssuedWriteAndRequiresInitialization()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;
        var issued = _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _writes.Reader.ReadAsync(Token);
        var queued = _device.SetAsync(Stopped() with { Speed = 12 }, Token);

        var stop = _device.EmergencyStopAsync(Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(stop.IsCompleted);
        _device.CompleteDispatch();
        await issued; // Already sent, so it is reported as written; the Stop follows it.
        await stop;

        var zero = await _writes.Reader.ReadAsync(Token);
        Assert.True(zero.SafetyStop);
        Assert.Equal(0, zero.Settings.Speed);
        Assert.False(_writes.Reader.TryRead(out _));
        Assert.False(_device.IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _device.SetAsync(Stopped() with { Speed = 10 }, Token));

        await _device.InitializeAsync(Token);

        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task EmergencyStopDuringInitializationLeavesTheDeviceNotReady()
    {
        ConnectStopped();
        _device.BlockPreparation = true;
        var initializing = _device.InitializeAsync(Token);
        await _device.PreparationStarted.WaitAsync(Token);

        var stop = _device.EmergencyStopAsync(Token);
        Assert.False(stop.IsCompleted);
        _device.CompletePreparation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);
        await stop;
        Assert.False(_device.IsReady);
    }

    [Fact]
    public async Task ResetWaitsForAnOwnedStopAndNeverRestoresMotion()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;
        var stop = _device.EmergencyStopAsync(Token);
        await _writes.Reader.ReadAsync(Token);

        var reset = _device.InitializeAsync(Token);

        Assert.False(reset.IsCompleted);
        Assert.False(_device.IsReady);
        _device.CompleteDispatch();
        await stop;
        await reset;
        var resetWrite = await _writes.Reader.ReadAsync(Token);
        Assert.True(resetWrite.SafetyStop);
        Assert.Equal(0, resetWrite.Settings.Speed);
        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task FaultIsPublishedAfterReadinessIsCleared()
    {
        await ConnectReadyAsync();
        var readyAtFault = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _device.DeviceStateChanged += change =>
        {
            if (change.IsT1)
                readyAtFault.TrySetResult(_device.IsReady);
        };

        _device.ReportState(Stopped() with { Speed = 5 }, _clock.GetUtcNow().UtcDateTime);

        Assert.False(await readyAtFault.Task.WaitAsync(Token));
    }

    [Fact]
    public async Task EmergencyStopBeforeAnyReportStillWritesSpeedZero()
    {
        _device.Connect();

        await _device.EmergencyStopAsync(Token);

        var zero = await _writes.Reader.ReadAsync(Token);
        Assert.True(zero.SafetyStop);
        Assert.Equal(0, zero.Settings.Speed);
        Assert.False(_device.IsReady);
    }

    [Fact]
    public async Task FirstReportDuringAPreReportStopDoesNotMakeItsZeroLookExternal()
    {
        _device.Connect();
        _device.BlockDispatch = true;
        var stop = _device.EmergencyStopAsync(Token);
        await _writes.Reader.ReadAsync(Token);

        _device.ReportState(Stopped() with { Speed = 10 }, _clock.GetUtcNow().UtcDateTime);
        _device.CompleteDispatch();
        await stop;
        _device.ReportState(Stopped(), _clock.GetUtcNow().UtcDateTime);

        Assert.False(_faults.Reader.TryRead(out _));
        Assert.False(_writes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task LateReportOfAnEarlierWriteIsNotAnExternalChange()
    {
        await ConnectReadyAsync();

        await _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _device.SetAsync(Stopped() with { Speed = 20 }, Token);
        _device.ReportState(Stopped() with { Speed = 10 }, _clock.GetUtcNow().UtcDateTime);
        _device.ReportState(Stopped() with { Speed = 20 }, _clock.GetUtcNow().UtcDateTime);

        Assert.False(_faults.Reader.TryRead(out _));
        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task OwnWriteReportedLongAfterItWasSentIsNotAnExternalChange()
    {
        await ConnectReadyAsync();

        await _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        _clock.Advance(TimeSpan.FromSeconds(10));
        _device.ReportState(Stopped() with { Speed = 10 }, _clock.GetUtcNow().UtcDateTime);

        Assert.False(_faults.Reader.TryRead(out _));
        Assert.True(_device.IsReady);
    }

    [Fact]
    public async Task ExternalChangeIsReportedAndStopsTheDevice()
    {
        await ConnectReadyAsync();

        _device.ReportState(Stopped() with { Speed = 5 }, _clock.GetUtcNow().UtcDateTime);

        Assert.IsType<UnexpectedMotionChangeException>(await _faults.Reader.ReadAsync(Token));
        var zero = await _writes.Reader.ReadAsync(Token);
        Assert.True(zero.SafetyStop);
        Assert.Equal(0, zero.Settings.Speed);
        Assert.False(_device.IsReady);
    }

    [Fact]
    public async Task SteadyMotionWithoutNewReportsStaysReady()
    {
        await ConnectReadyAsync();
        _device.ReportStateDuringDispatch = true;
        await _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _writes.Reader.ReadAsync(Token);

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.True(_device.IsReady);
        Assert.False(_faults.Reader.TryRead(out _));
        Assert.False(_writes.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisconnectCancelsQueuedCommandsAndClearsReadiness()
    {
        await ConnectReadyAsync();
        _device.BlockDispatch = true;
        var issued = _device.SetAsync(Stopped() with { Speed = 10 }, Token);
        await _writes.Reader.ReadAsync(Token);
        var queued = _device.SetAsync(Stopped() with { Speed = 12 }, Token);

        _device.Disconnect();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(_device.IsReady);
        Assert.Null(_device.LatestState);
        _device.CompleteDispatch();
        await issued;
    }

    public async ValueTask DisposeAsync()
    {
        _device.CompleteDispatch();
        await _device.DisposeAsync();
    }

    private static MotionSettings Stopped() => new(
        MotionDeviceState.StrokeEngineIdle,
        0,
        10,
        50,
        20,
        0,
        MotionPattern.SimpleStroke,
        0);

    private void ConnectStopped(MotionSettings? stopped = null)
    {
        _device.Connect();
        _device.ReportState(stopped ?? Stopped(), _clock.GetUtcNow().UtcDateTime);
    }

    private async Task ConnectReadyAsync(MotionSettings? stopped = null)
    {
        ConnectStopped(stopped);
        await _device.InitializeAsync(Token);
        await _writes.Reader.ReadAsync(Token);
    }
}
