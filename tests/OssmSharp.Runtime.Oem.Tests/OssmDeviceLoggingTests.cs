using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceLoggingTests
{
    [Fact]
    public async Task ConnectionRequestsLogAtDebugWithoutSensitiveFields()
    {
        var logger = new RecordingLogger<DeviceBase>();
        using var context = new BleTestContext { Logger = logger };
        await using var device = await context.ConnectAsync(TestContext.Current.CancellationToken);

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
        var logger = new RecordingLogger<DeviceBase>();
        using var context = new BleTestContext { Logger = logger };
        using var access = new Subject<AccessState>();
        context.Manager.Setup(manager => manager.RequestAccess()).Returns(access);
        await using var device = context.CreateDevice();
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);
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
