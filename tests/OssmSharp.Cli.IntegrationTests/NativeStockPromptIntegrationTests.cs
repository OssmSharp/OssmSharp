#if WINDOWS || MACOS
using OssmSharp.Cli;
using OssmSharp.Cli.Models;
using OssmSharp.Device.Stock;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.IntegrationTests;

public sealed class NativeStockPromptIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QualifiedBluetoothPromptRoutesSelectionWritesSupportedGeometryAndCleansUp()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var composition = new NativeStockTestComposition(
            new NativeStockTestQualificationResolver(maximumSpeedPercent: 80));
        using var output = new SignalingTextWriter("Device write settled; check status for reported state.");
        using var errors = new SignalingTextWriter();
        using var input = new GatedPromptReader(
            [
                "initialize",
                "set --speed 12 --stroke 25 --depth 50 --pattern SimpleStroke",
                "status",
                "quit",
            ],
            2,
            Task.WhenAny(output.Written, errors.Written));

        var result = await composition.CreatePromptSession(input, output, errors)
            .RunAsync(PromptSelection.Parse("selected-stock-device", null), timeout.Token);

        Assert.Equal(0, result);
        Assert.True(errors.Content.Length == 0, errors.Content);
        Assert.Equal(
            [
                "set:speed:0",
                "set:speed:0",
                "set:speed:0",
                "set:stroke:25",
                "set:speed:0",
                "set:depth:50",
                "set:speed:12",
                "set:speed:0",
            ],
            composition.Transport.GetCommands());
        Assert.Same(composition.Discovery.Selected, composition.Transport.LastSelected);
        Assert.Equal(1, composition.Discovery.ScanCount);
        Assert.Equal(1, composition.Transport.KnobLimitingPreparations);
        Assert.Equal(1, composition.Transport.DisposeCount);
        Assert.Contains("ConnectionState: Connected", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Readiness: Ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Submitted: speed 12%", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("effective speed 10%", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(composition.Transport.GetCommands(), command => command.StartsWith("set:sensation:", StringComparison.Ordinal));
        Assert.DoesNotContain(composition.Transport.GetCommands(), command => command.StartsWith("set:buffer:", StringComparison.Ordinal));
        Assert.Equal(string.Empty, errors.ToString());
        Assert.Equal(0, composition.SimulatorFactoryCalls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DefaultDiQualificationRejectsMotionWithoutFaultOrNonzeroWrite()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var composition = new NativeStockTestComposition();
        Assert.IsType<UnqualifiedStockMotion>(composition.QualificationResolver);
        using var output = new SignalingTextWriter();
        using var errors = new SignalingTextWriter("Stock motion lacks a qualified mapping for this connection.");
        using var input = new GatedPromptReader(
            [
                "initialize",
                "set --speed 12 --stroke 20 --depth 50 --pattern SimpleStroke",
                "status",
                "quit",
            ],
            2,
            errors.Written);

        var result = await composition.CreatePromptSession(input, output, errors)
            .RunAsync(PromptSelection.Parse("selected-stock-device", null), timeout.Token);

        Assert.Equal(0, result);
        Assert.Equal(["set:speed:0", "set:speed:0"], composition.Transport.GetCommands());
        Assert.Same(composition.Discovery.Selected, composition.Transport.LastSelected);
        Assert.Equal(1, composition.Discovery.ScanCount);
        Assert.Equal(1, composition.Transport.DisposeCount);
        Assert.Contains(
            "Stock motion lacks a qualified mapping for this connection.",
            errors.Content,
            StringComparison.Ordinal);
        Assert.Contains("Readiness: Ready", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Device feedback error", errors.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("set:speed:12", composition.Transport.GetCommands());
    }
}
#endif
