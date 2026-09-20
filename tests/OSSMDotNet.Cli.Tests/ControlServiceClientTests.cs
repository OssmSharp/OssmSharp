using System.Text.Json;
using OSSMDotNet.ControlClient.Tests;

namespace OSSMDotNet.Cli.Tests;

public sealed class ControlServiceClientTests
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    [Fact]
    public async Task StatusRendersTheCurrentHubStateAndDoesNotExposeTheServiceKey()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.SendOperatorStateOnSubscribe = true;

        var result = await InvokeAsync(fixture, "status", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(JsonSerializer.Serialize(LocalControlHubFixture.OperatorStatus) + Environment.NewLine, result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.DoesNotContain(LocalControlHubFixture.ServiceKeyValue, result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task StopAcknowledgementUsesTheAcceptedButUnverifiedWarningAndDoesNotRetry()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.SendOperatorStateOnSubscribe = true;

        var result = await InvokeAsync(fixture, "stop", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Stop request accepted. Wait for status feedback before treating motion as stopped." + Environment.NewLine,
            result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(["SubscribeOperator", "Stop"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
    }

    [Fact]
    public async Task MissingOrInvalidServiceKeyExitsOneBeforeOpeningTheFixtureConnection()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);

        var missing = await InvokeAsync(fixture, "status", null, cancellationSource.Token);
        var invalid = await InvokeAsync(fixture, "stop", "not-a-service-key", cancellationSource.Token);

        Assert.Equal(1, missing.ExitCode);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Empty(missing.StandardOutput);
        Assert.Empty(invalid.StandardOutput);
        Assert.Equal("Set OSSM_CONTROL_CLIENT_SERVICE_KEY to a ControlService-authorized service key." + Environment.NewLine,
            missing.StandardError);
        Assert.Equal(missing.StandardError, invalid.StandardError);
        Assert.Equal(0, fixture.ConnectionCount);
        Assert.Empty(fixture.Invocations);
    }

    [Fact]
    public async Task RejectedStatusExitsOneWithTheSafeRejectionAndDoesNotRetry()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.Reject("SubscribeOperator", "subscription-rejected");

        var result = await InvokeAsync(fixture, "status", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("Control request rejected: subscription-rejected." +
            Environment.NewLine, result.StandardError);
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task UnknownStatusOutcomeExitsOneAndDoesNotRetry()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.Hold("SubscribeOperator");
        try
        {
            var command = InvokeAsync(fixture, "status", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);
            await fixture.WaitForInvocationAsync("SubscribeOperator", cancellationSource.Token);

            var result = await command;

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("The command outcome is unknown. Do not resend it automatically." + Environment.NewLine,
                result.StandardError);
            Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
        }
        finally
        {
            fixture.Release("SubscribeOperator");
        }
    }

    [Fact]
    public async Task CancelledStatusExitsOneAndDoesNotRetry()
    {
        using var testCancellation = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(testCancellation.Token);
        fixture.Hold("SubscribeOperator");
        using var callerCancellation = new CancellationTokenSource();
        try
        {
            var command = InvokeAsync(fixture, "status", LocalControlHubFixture.ServiceKeyValue, callerCancellation.Token);
            await fixture.WaitForInvocationAsync("SubscribeOperator", testCancellation.Token);
            await callerCancellation.CancelAsync();

            var result = await command;

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("Control request was cancelled before a confirmed result." + Environment.NewLine,
                result.StandardError);
            Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
        }
        finally
        {
            fixture.Release("SubscribeOperator");
        }
    }

    [Fact]
    public async Task RejectedStopExitsOneWithTheSafeRejectionAndDoesNotRetry()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.SendOperatorStateOnSubscribe = true;
        fixture.Reject("Stop", "operator-locked");

        var result = await InvokeAsync(fixture, "stop", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("Control request rejected: operator-locked." +
            Environment.NewLine, result.StandardError);
        Assert.Equal(["SubscribeOperator", "Stop"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
    }

    [Fact]
    public async Task UnknownStopOutcomeExitsOneAndDoesNotRetry()
    {
        using var cancellationSource = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        fixture.SendOperatorStateOnSubscribe = true;
        fixture.Hold("Stop");
        try
        {
            var command = InvokeAsync(fixture, "stop", LocalControlHubFixture.ServiceKeyValue, cancellationSource.Token);
            await fixture.WaitForInvocationAsync("Stop", cancellationSource.Token);

            var result = await command;

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("The command outcome is unknown. Do not resend it automatically." + Environment.NewLine,
                result.StandardError);
            Assert.Equal(["SubscribeOperator", "Stop"], fixture.Invocations.Select(invocation => invocation.Method));
            Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
        }
        finally
        {
            fixture.Release("Stop");
        }
    }

    [Fact]
    public async Task CancelledStopExitsOneAndDoesNotRetry()
    {
        using var testCancellation = CreateTestCancellationSource();
        await using var fixture = await LocalControlHubFixture.StartAsync(testCancellation.Token);
        fixture.SendOperatorStateOnSubscribe = true;
        fixture.Hold("Stop");
        using var callerCancellation = new CancellationTokenSource();
        try
        {
            var command = InvokeAsync(fixture, "stop", LocalControlHubFixture.ServiceKeyValue, callerCancellation.Token);
            await fixture.WaitForInvocationAsync("Stop", testCancellation.Token);
            await callerCancellation.CancelAsync();

            var result = await command;

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Equal("Control request was cancelled before a confirmed result." + Environment.NewLine,
                result.StandardError);
            Assert.Equal(["SubscribeOperator", "Stop"], fixture.Invocations.Select(invocation => invocation.Method));
            Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
        }
        finally
        {
            fixture.Release("Stop");
        }
    }

    private static CancellationTokenSource CreateTestCancellationSource()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(8));
        return source;
    }

    private static async Task<CliResult> InvokeAsync(
        LocalControlHubFixture fixture, string operation, string? serviceKey, CancellationToken cancellationToken)
    {
        await ConsoleGate.WaitAsync(TestContext.Current.CancellationToken);
        var priorOutput = Console.Out;
        var priorError = Console.Error;
        var priorKey = Environment.GetEnvironmentVariable("OSSM_CONTROL_CLIENT_SERVICE_KEY");
        using var standardOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var standardError = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Environment.SetEnvironmentVariable("OSSM_CONTROL_CLIENT_SERVICE_KEY", serviceKey);
            Console.SetOut(standardOutput);
            Console.SetError(standardError);
            var exitCode = await ControlCommand.Create()
                .Parse(["--control-origin", fixture.ControlOrigin, operation])
                .InvokeAsync(cancellationToken: cancellationToken);
            return new(exitCode, standardOutput.ToString(), standardError.ToString());
        }
        finally
        {
            Console.SetOut(priorOutput);
            Console.SetError(priorError);
            Environment.SetEnvironmentVariable("OSSM_CONTROL_CLIENT_SERVICE_KEY", priorKey);
            ConsoleGate.Release();
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
