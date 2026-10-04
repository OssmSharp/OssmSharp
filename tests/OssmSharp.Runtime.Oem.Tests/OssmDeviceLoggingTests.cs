using System.Reactive.Subjects;
using FastMoq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceLoggingTests : MockerTestBase<OssmDevice>, IAsyncDisposable
{
    private readonly RecordingLogger<OssmDevice> logger = new();
    private BleTestContext context = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        context = new(mocks) { Logger = logger };
        mocks.AddType<string>("device-1");
        mocks.AddType<ILogger<OssmDevice>>(logger);
        mocks.AddType<IOptions<OssmDeviceOptions>>(Options.Create(context.DeviceOptions));
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            try
            {
                context.Dispose();
            }
            finally
            {
                base.Dispose();
            }
        }
    }

    [Fact]
    public async Task LifecycleAndApiCallsLogAtInformationWhileCommandsLogAtDebug()
    {
        var device = Component;
        var token = TestContext.Current.CancellationToken;
        var settings = new MotionSettings(StrokePattern.SimpleStroke, 20, 30, 40, 50);

        Assert.Null(await device.GetStateAsync(cancellationToken: token));
        await context.ConnectAsync(device, token);
        await device.ConnectAsync(token);
        Assert.Equal(new DeviceState(0, 0, 50, 10, 50), await device.GetStateAsync(cancellationToken: token));
        await context.StreamUntilAsync(device.GetStateAsync(true, token), token);
        await device.ResetAsync(token);
        var result = await device.SetAsync(settings, token);
        await device.EmergencyStopAsync(token);
        await device.ResetAsync(token);
        await device.DisconnectAsync(token);
        await device.GetStateAsync(cancellationToken: token);
        await device.DisposeAsync();
        await device.DisposeAsync();

        Assert.Equal(SetStatus.Completed, result.Status);
        Assert.Equal(
            ["connecting", "connected", "disconnecting", "disconnected"],
            logger.Entries.Where(entry => entry.Id.Id == 18 && entry.Level == LogLevel.Information)
                .Select(entry => entry.StructuredState.Single(field => field.Key == "ConnectionState").Value));
        Assert.Equal(
            ["GetState", "Connect", "Connect", "GetState", "GetState", "Reset", "Set", "Stop", "Reset", "Disconnect", "GetState", "Dispose", "Dispose"],
            logger.Entries.Where(entry => entry.Id.Id == 17 && entry.Level == LogLevel.Information)
                .Select(entry => entry.StructuredState.Single(field => field.Key == "Operation").Value));
        Assert.Single(logger.Entries, entry => entry.Id.Id == 17
            && entry.StructuredState.Any(field => field.Key == "Operation" && Equals(field.Value, "Stop")));
        var finished = Assert.Single(logger.Entries, entry => entry.Id.Id == 19);
        Assert.Equal(LogLevel.Information, finished.Level);
        Assert.Contains(finished.StructuredState, field => field.Key == "Status" && Equals(field.Value, SetStatus.Completed));
        Assert.Contains(finished.StructuredState, field => field.Key == "CommandsWritten" && Equals(field.Value, result.CommandsWritten));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Id.Id == 20 && entry.Level == LogLevel.Debug));
        Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id is 11 or 12
            && entry.StructuredState.Any(field => field.Key == "Operation" && Equals(field.Value, "Stop")));
        Assert.Equal(
            settings.ToCommands().Select(command => command.Setting),
            logger.Entries.Where(entry => entry.Id.Id == 21 && entry.Level == LogLevel.Debug)
                .Select(entry => (FirmwareSetting)entry.StructuredState.Single(field => field.Key == "Setting").Value!));
        Assert.Equal(result.CommandsWritten, logger.Entries.Count(entry => entry.Id.Id == 22 && entry.Level == LogLevel.Debug));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Id.Id is 14 or 21 or 22);
    }

    [Fact]
    public async Task PendingInitialObservationLogsWaitWithoutConnectedState()
    {
        var device = Component;
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(connect.IsCompleted);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Id.Id == 14
                && entry.Message.Contains("waiting for initial motion observation", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 18
                && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
        }
        finally
        {
            await context.StreamUntilAsync(connect, TestContext.Current.CancellationToken);
        }

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Id.Id == 18
            && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
    }

    [Fact]
    public async Task ConnectionRequestsLogAtDebugWithoutSensitiveFields()
    {
        await context.ConnectAsync(Component, TestContext.Current.CancellationToken);

        Assert.Equal(
            context.Requests.Select(request => request["id"]!.GetValue<uint>()),
            logger.Entries.Where(entry => entry.Id.Id == 23 && entry.Level == LogLevel.Debug)
                .Select(entry => (uint)entry.StructuredState.Single(field => field.Key == "RequestId").Value!));
        Assert.Equal(2, logger.Entries.Count(entry => entry.Id.Id == 24 && entry.Level == LogLevel.Debug));
        Assert.Contains(logger.Entries, entry => entry.Id.Id == 14 && entry.Level == LogLevel.Debug
            && entry.Message.Contains("requesting bluetooth access", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.StructuredState.Any(field => field.Key is "Lease" or "Payload" or "Identifier"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Id.Id is 14 or 15 or 23 or 24);
    }

    [Fact]
    public async Task PendingBluetoothAccessLogsStageWithoutConnectedState()
    {
        using var access = new Subject<AccessState>();
        context.Manager.Setup(manager => manager.RequestAccess()).Returns(access);
        var connect = Component.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(connect.IsCompleted);
            Assert.Empty(context.Requests);
            Assert.Contains(logger.Entries, entry => entry.Id.Id == 14 && entry.Level == LogLevel.Debug
                && entry.Message.Contains("requesting bluetooth access", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 18
                && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
        }
        finally
        {
            access.OnNext(AccessState.Denied);
            access.OnCompleted();
            await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
        }

        Assert.Contains(logger.Entries, entry => entry.Id.Id == 2 && entry.Level == LogLevel.Error);
        Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 18
            && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
    }
}
