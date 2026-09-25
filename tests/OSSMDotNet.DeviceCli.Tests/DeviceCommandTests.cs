using OSSMDotNet.DeviceCli.Commands;

namespace OSSMDotNet.DeviceCli.Tests;

/// <summary>
/// Verifies device CLI parsing and forwarding of bounded simulator commands.
/// </summary>
public sealed class DeviceCommandTests
{
    [Fact]
    public void StartRequiresAnAbsoluteExistingSimulatorPath()
    {
        var command = DeviceCommand.Create((_, _) => Task.FromResult(0));

        var missing = command.Parse(["start"]);
        var relative = command.Parse(["start", "--simulator-path", "simulator"]);
        var absent = command.Parse([
            "start",
            "--simulator-path",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        ]);

        Assert.NotEmpty(missing.Errors);
        Assert.Single(relative.Errors);
        Assert.Single(absent.Errors);
    }

    [Fact]
    public async Task InvocationReturnsTwoForUsageErrorsWithoutRunningTheDevice()
    {
        var invoked = false;

        var exitCode = await DeviceCommand.InvokeAsync(
            ["start"],
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task StartForwardsTheSimulatorPath()
    {
        var path = CreateTemporaryFile();
        DeviceCommandRequest? request = null;
        try
        {
            var command = DeviceCommand.Create((value, _) =>
            {
                request = value;
                return Task.FromResult(0);
            });

            var exitCode = await command.Parse(["start", "--simulator-path", path])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.NotNull(request);
            Assert.Equal(DeviceCommandOperation.Start, request.Operation);
            Assert.Equal(Path.GetFullPath(path), request.SimulatorPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IdentifyAllowsDiscoveryWithoutAnIdButRejectsABlankId()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        Assert.NotEmpty(command.Parse(["identify", "--peripheral-id", " "]).Errors);
        var exitCode = await command.Parse(["identify"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Identify, request.Operation);
        Assert.Null(request.PeripheralId);
    }

    [Fact]
    public async Task IdentifyForwardsOnlyTheSelectedPeripheral()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse(["identify", "--peripheral-id", "selected-device"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Identify, request.Operation);
        Assert.Equal("selected-device", request.PeripheralId);
        Assert.Null(request.SimulatorPath);
    }

    [Fact]
    public async Task IdentifyDoesNotUseTheSimulatorHost()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var endpoint = SimulatorEndpoint.CreateDefault();

        var exitCode = await DeviceCommandRunner.RunAsync(
            new DeviceCommandRequest(DeviceCommandOperation.Identify, PeripheralId: "selected-device"),
            output,
            error,
            endpoint,
            (_, _) => throw new InvalidOperationException("Simulator startup is forbidden."),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("macOS", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("status", "Status")]
    [InlineData("stop", "Stop")]
    public async Task LifecycleCommandsTargetTheRunningHost(
        string commandName,
        string operation)
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse([commandName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(operation, request.Operation.ToString());
        Assert.Null(request.SimulatorPath);
    }

    [Fact]
    public async Task MoveForwardsValidatedBoundedMotion()
    {
        DeviceCommandRequest? request = null;
        var command = DeviceCommand.Create((value, _) =>
        {
            request = value;
            return Task.FromResult(0);
        });

        var exitCode = await command.Parse([
            "move",
            "--speed", "25",
            "--stroke", "30",
            "--depth", "60",
            "--seconds", "1.5",
        ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(request);
        Assert.Equal(DeviceCommandOperation.Move, request.Operation);
        Assert.Null(request.SimulatorPath);
        Assert.Equal(25, request.Speed);
        Assert.Equal(30, request.Stroke);
        Assert.Equal(60, request.Depth);
        Assert.Equal(TimeSpan.FromSeconds(1.5), request.Duration);
    }

    [Theory]
    [InlineData("0", "20", "50", "1")]
    [InlineData("101", "20", "50", "1")]
    [InlineData("20", "0", "50", "1")]
    [InlineData("20", "60", "50", "1")]
    [InlineData("20", "20", "50", "0")]
    [InlineData("20", "20", "50", "61")]
    public void MoveRejectsUnsafeOrUnsupportedValues(string speed, string stroke, string depth, string seconds)
    {
        var parse = DeviceCommand.Create((_, _) => Task.FromResult(0)).Parse([
            "move",
            "--speed", speed,
            "--stroke", stroke,
            "--depth", depth,
            "--seconds", seconds,
        ]);

        Assert.NotEmpty(parse.Errors);
    }

    private static string CreateTemporaryFile()
    {
        var path = Path.GetTempFileName();
        return Path.GetFullPath(path);
    }
}
