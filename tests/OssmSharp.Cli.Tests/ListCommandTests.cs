using AwesomeAssertions;
using FastMoq;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class ListCommandTests : MockerTestBase<ListCommand>, IAsyncDisposable
{
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly RecordingLogger<ListCommand> logger = new();
    private CommandTestContext context = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        context = new CommandTestContext(mocks);
        mocks.AddType(context.Discovery);
        mocks.AddKeyedType<TextWriter>("output", output);
        mocks.AddKeyedType<TextWriter>("error", error);
        mocks.AddType<ILogger<ListCommand>>(logger);
    };

    public ValueTask DisposeAsync()
    {
        try
        {
            context.Dispose();
            output.Dispose();
            error.Dispose();
        }
        finally
        {
            base.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ListWritesIdentifiersAndNamesWithProgressOnStderr(int count)
    {
        DiscoveredDevice[] available = [new("Device-A", "First OSSM"), new("Device-B", "Second OSSM")];
        var run = Component.RunAsync(TestContext.Current.CancellationToken);

        foreach (var device in available.Take(count))
        {
            context.Advertise(device.Identifier, device.Name);
        }

        context.Clock.Advance(TimeSpan.FromSeconds(5));

        (await run).Should().Be(count == 0 ? 1 : 0);
        context.Scans.Should().Be(1);
        context.ScanDisposals.Should().Be(1);
        var expected = count switch
        {
            0 => string.Empty,
            1 => $"Device-A  First OSSM{Environment.NewLine}",
            _ => $"Device-A  First OSSM{Environment.NewLine}Device-B  Second OSSM{Environment.NewLine}",
        };
        output.ToString().Should().Be(expected);
        error.ToString().Should().Be($"Scanning for nearby devices...{Environment.NewLine}"
            + (count == 0 ? $"No devices found.{Environment.NewLine}" : string.Empty));
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Id.Id.Should().Be(1);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Be($"Discovery completed with {count} devices.");
        entry.StructuredState.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["DeviceCount"] = count,
            ["{OriginalFormat}"] = "Discovery completed with {DeviceCount} devices.",
        });
    }

    [Fact]
    public async Task ListDiscoveryFailureReturnsOneAndLogsOnlyFailureType()
    {
        var run = Component.RunAsync(TestContext.Current.CancellationToken);

        context.ScanResults.OnError(new IOException("scan failed for Device-A: private payload"));

        (await run).Should().Be(1);
        context.ScanDisposals.Should().Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("Scanning").And.Contain("ERROR discovery: scan failed").And.Contain("Check bluetooth access and that the device is turned on.");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Id.Id.Should().Be(3);
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Be("Command list failed with IOException.");
        entry.StructuredState.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["Command"] = "list",
            ["FailureType"] = nameof(IOException),
            ["{OriginalFormat}"] = "Command {Command} failed with {FailureType}.",
        });
    }

    [Fact]
    public async Task ListCancellationReleasesScanAndLogsDebugWithoutError()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = Component.RunAsync(cancellation.Token);

        await cancellation.CancelAsync();

        (await run).Should().Be(0);
        context.ScanDisposals.Should().Be(1);
        context.ScanResults.HasObservers.Should().BeFalse();
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Be($"Scanning for nearby devices...{Environment.NewLine}Cancelled. Waiting for pending work and cleanup.{Environment.NewLine}");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Id.Id.Should().Be(2);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Exception.Should().BeNull();
        entry.StructuredState.Should().Contain(new KeyValuePair<string, object?>("Command", "list"));
    }
}
