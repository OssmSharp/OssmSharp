namespace OSSMDotNet.DeviceCli.Tests;

public sealed class DeviceCommandTests
{
    [Fact]
    public void StatusRequiresAnAbsoluteExistingSimulatorPath()
    {
        var command = DeviceCommand.Create((_, _) => Task.FromResult(0));

        var missing = command.Parse(["status"]);
        var relative = command.Parse(["--simulator-path", "simulator", "status"]);
        var absent = command.Parse(["--simulator-path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "status"]);

        Assert.NotEmpty(missing.Errors);
        Assert.Single(relative.Errors);
        Assert.Single(absent.Errors);
    }

    [Fact]
    public async Task InvocationReturnsTwoForUsageErrorsWithoutRunningTheDevice()
    {
        var invoked = false;

        var exitCode = await DeviceCommand.InvokeAsync(
            ["status"],
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
    public async Task StatusForwardsTheSimulatorPath()
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

            var exitCode = await command.Parse(["--simulator-path", path, "status"])
                .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.NotNull(request);
            Assert.Equal(DeviceCommandOperation.Status, request.Operation);
            Assert.Equal(Path.GetFullPath(path), request.SimulatorPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MoveForwardsValidatedBoundedMotion()
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

            var exitCode = await command.Parse([
                "--simulator-path", path,
                "move",
                "--speed", "25",
                "--stroke", "30",
                "--depth", "60",
                "--seconds", "1.5",
            ]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.NotNull(request);
            Assert.Equal(DeviceCommandOperation.Move, request.Operation);
            Assert.Equal(25, request.Speed);
            Assert.Equal(30, request.Stroke);
            Assert.Equal(60, request.Depth);
            Assert.Equal(TimeSpan.FromSeconds(1.5), request.Duration);
        }
        finally
        {
            File.Delete(path);
        }
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
        var path = CreateTemporaryFile();
        try
        {
            var parse = DeviceCommand.Create((_, _) => Task.FromResult(0)).Parse([
                "--simulator-path", path,
                "move",
                "--speed", speed,
                "--stroke", stroke,
                "--depth", depth,
                "--seconds", seconds,
            ]);

            Assert.NotEmpty(parse.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryFile()
    {
        var path = Path.GetTempFileName();
        return Path.GetFullPath(path);
    }
}
