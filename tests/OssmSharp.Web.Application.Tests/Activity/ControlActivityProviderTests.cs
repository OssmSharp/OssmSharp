using Microsoft.Extensions.Logging;
using OssmSharp.Web.Application.Activity;

namespace OssmSharp.Web.Application.Tests.Activity;

/// <summary>
/// Verifies safe activity capture, bounded retention, and incremental reads.
/// </summary>
public sealed partial class ControlActivityProviderTests
{
    [Fact]
    public void LiveLogIncludesOnlyExplicitSafeActivityWithoutExceptionsOrScopes()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger(typeof(ControlOperations).FullName!);
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
    public void LegacyLoggerCategoryDoesNotPublishOperatorActivity()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OSSMDotNet.ExternalPlugin");

        LogOperatorActivity(logger, LogLevel.Information, "Legacy category message", null);

        Assert.Empty(provider.ReadAfter(0));
    }

    [Fact]
    public void LiveLogRetainsOnlyTwoHundredEntriesAndSupportsIncrementalReads()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger("OssmSharp.Device.Simulator.SimulatedDevice");
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
        var logger = provider.CreateLogger(typeof(ControlOperations).FullName!);
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
        var logger = provider.CreateLogger("OssmSharp.Web.Application.FutureComponent");

        LogUnrelatedNumericEvent(logger);

        Assert.Empty(provider.ReadAfter(0));
    }

    [Fact]
    public void OperationalAuthorityEventsAreNotOperatorActivity()
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        var logger = provider.CreateLogger(typeof(ControlAuthority).FullName!);

        ControlServiceLog.GuestAdmitted(logger);
        ControlServiceLog.GuestRevoked(logger, "guest-transport-lost");
        ControlServiceLog.GuestStopAccepted(logger);
        ControlServiceLog.SafetyState(logger, "Locked", "external-parameter-change");

        Assert.Empty(provider.ReadAfter(0));
    }

    [Theory]
    [InlineData(0, 0, false, 1, false)]
    [InlineData(250, 250, false, 51, false)]
    [InlineData(250, 300, false, 51, false)]
    [InlineData(250, 0, true, 251, true)]
    [InlineData(250, 250, true, 251, false)]
    public void EmptyPagesPreserveRetentionAndGapMetadata(
        int entryCount, long cursor, bool dispose, long retainedFirst, bool gap)
    {
        using var provider = new ControlActivityProvider(TimeProvider.System);
        for (var index = 0; index < entryCount; index++)
            provider.Append(LogLevel.Information, "Activity");
        if (dispose)
            provider.Dispose();

        var page = provider.ReadBatchAfter(cursor, static _ => 0, 1);

        Assert.Equal(retainedFirst, page.RetainedFirstSequence);
        Assert.Equal(entryCount, page.RetainedLastSequence);
        Assert.Equal(0, page.FirstSequence);
        Assert.Equal(0, page.LastSequence);
        Assert.Null(page.ContinuationSequence);
        Assert.Equal(gap, page.Gap);
        Assert.Empty(page.Entries);
    }

    [Fact]
    public void CaughtUpReadsDoNotAllocateInProportionToRetainedEntries()
    {
        using var empty = new ControlActivityProvider(TimeProvider.System);
        using var full = new ControlActivityProvider(TimeProvider.System);
        for (var index = 0; index < 200; index++)
            full.Append(LogLevel.Information, "Activity");
        MeasureCaughtUpReads(empty, 0);
        MeasureCaughtUpReads(full, 200);

        var emptyBytes = MeasureCaughtUpReads(empty, 0);
        var fullBytes = MeasureCaughtUpReads(full, 200);

        Assert.InRange(fullBytes, 0, emptyBytes + 128 * 32);
        Assert.Equal(200, full.ReadAfter(0).Count);
    }

    private static long MeasureCaughtUpReads(ControlActivityProvider provider, long cursor)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
            GC.KeepAlive(provider.ReadBatchAfter(cursor, static _ => 0, 1));
        return GC.GetAllocatedBytesForCurrentThread() - before;
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
