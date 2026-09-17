using Microsoft.Extensions.Logging;
using OSSMDotNet.ControlService;

namespace OSSMDotNet.ControlService.Tests;

public sealed partial class ControlActivityProviderTests
{
    [Fact]
    public void LiveLogIncludesOnlyExplicitSafeActivityWithoutExceptionsOrScopes()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.ControlService.ControlOperations");
        using var scope = logger.BeginScope("scope-secret");
        LogUnmarkedSecret(logger);
        LogOperatorActivity(logger, LogLevel.Error, "Connection unavailable.", new IOException("exception-secret"));
        LogOperatorActivity(provider.CreateLogger("ThirdParty"), LogLevel.Information, "third-party-secret", null);

        var entry = Assert.Single(provider.ReadAfter(0));
        Assert.Equal("Connection unavailable.", entry.Message);
        Assert.Equal(LogLevel.Error.ToString(), entry.Level);
        Assert.DoesNotContain("secret", entry.Message);
    }

    [Fact]
    public void LiveLogRetainsOnlyTwoHundredEntriesAndSupportsIncrementalReads()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.DeviceBridge.SimulatorProcessBridge");
        for (var index = 0; index < 250; index++)
            LogDeviceActivity(logger, index);

        var entries = provider.ReadAfter(0);
        Assert.Equal(200, entries.Count);
        Assert.Equal(51, entries[0].Sequence);
        Assert.Equal(250, entries[^1].Sequence);
        Assert.Equal(2, provider.ReadAfter(248).Count);
        Assert.Empty(provider.ReadAfter(250));
    }

    [Fact]
    public void DisposalClearsTheBufferAndStopsFurtherCapture()
    {
        var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.ControlService.ControlOperations");
        LogOperatorActivity(logger, LogLevel.Information, "Session ended.", null);
        Assert.Single(provider.ReadAfter(0));
        provider.Dispose();
        LogOperatorActivity(logger, LogLevel.Information, "Session created.", null);
        Assert.Empty(provider.ReadAfter(0));
    }

    [Fact]
    public void NumericEventIdsWithoutAnExplicitSafeEventNameAreNotCaptured()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.ControlService.FutureComponent");

        LogUnrelatedNumericEvent(logger);

        Assert.Empty(provider.ReadAfter(0));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "unmarked-secret")]
    private static partial void LogUnmarkedSecret(ILogger logger);

    [LoggerMessage(EventId = 200, EventName = "OperatorActivityOperation", Message = "{Message}")]
    private static partial void LogOperatorActivity(ILogger logger, LogLevel level, string message, Exception? exception);

    [LoggerMessage(EventId = 100, EventName = "DeviceActivity", Level = LogLevel.Information, Message = "Device activity {Index}.")]
    private static partial void LogDeviceActivity(ILogger logger, int index);

    [LoggerMessage(EventId = 200, EventName = "FutureComponentEvent", Level = LogLevel.Information, Message = "unreviewed message")]
    private static partial void LogUnrelatedNumericEvent(ILogger logger);

}
