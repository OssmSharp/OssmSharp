using Microsoft.Extensions.Logging;
using OSSMDotNet.AdminService;

namespace OSSMDotNet.AdminService.Tests;

public sealed class OperatorLogTests
{
    [Fact]
    public void LiveLogIncludesOnlyExplicitSafeActivityWithoutExceptionsOrScopes()
    {
        using var provider = new OperatorLogProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.AdminService.AdminOperations");
        using var scope = logger.BeginScope("scope-secret");
        logger.LogInformation("unmarked-secret");
        logger.LogError(new EventId(200, "OperatorActivity"), new IOException("exception-secret"), "Connection unavailable.");
        provider.CreateLogger("ThirdParty").LogInformation(new EventId(200, "OperatorActivity"), "third-party-secret");

        var entry = Assert.Single(provider.ReadAfter(0));
        Assert.Equal("Connection unavailable.", entry.Message);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain("secret", entry.Message);
    }

    [Fact]
    public void LiveLogRetainsOnlyTwoHundredEntriesAndSupportsIncrementalReads()
    {
        using var provider = new OperatorLogProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.DeviceBridge.SimulatorProcessBridge");
        for (var index = 0; index < 250; index++)
            logger.LogInformation(new EventId(100, "DeviceActivity"), "Device activity {Index}.", index);

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
        var provider = new OperatorLogProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.AdminService.AdminOperations");
        logger.LogInformation(new EventId(200, "OperatorActivity"), "Session ended.");
        Assert.Single(provider.ReadAfter(0));
        provider.Dispose();
        logger.LogInformation(new EventId(200, "OperatorActivity"), "Session created.");
        Assert.Empty(provider.ReadAfter(0));
    }
}
