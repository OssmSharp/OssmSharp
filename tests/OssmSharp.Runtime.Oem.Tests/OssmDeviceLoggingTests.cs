using System.Reactive.Subjects;
using FastMoq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceLoggingTests : MockerTestBase<OssmDevice>, IAsyncDisposable
{
    private readonly RecordingLogger<DeviceBase> logger = new();
    private BleTestContext context = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        context = new(mocks) { Logger = logger };
        mocks.AddType<string>("device-1");
        mocks.AddType<ILogger<DeviceBase>>(logger);
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
