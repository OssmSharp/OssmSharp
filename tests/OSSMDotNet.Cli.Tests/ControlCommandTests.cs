using System.CommandLine;
using OSSMDotNet.ControlClient.Tests;

namespace OSSMDotNet.Cli.Tests;

/// <summary>
/// Verifies that the control CLI accepts only valid local or secure remote service origins.
/// </summary>
public sealed class ControlCommandTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5181/")]
    [InlineData("http://[::1]:5181/")]
    [InlineData("https://control.example:443/")]
    public void ParserAcceptsOnlySupportedPathFreeOriginForms(string origin)
    {
        var command = ControlCommand.Create();
        Assert.Equal("OSSM control CLI. Configure OSSM_CONTROL_CLIENT_SERVICE_KEY before connecting.", command.Description);

        var parse = command.Parse(["--endpoint", origin, "status"]);

        Assert.Empty(parse.Errors);
        Assert.Empty(parse.UnmatchedTokens);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public void ParserDefaultsToLoopbackEndpoint(string operation)
    {
        var command = ControlCommand.Create();
        var parse = command.Parse([operation]);
        var endpoint = Assert.IsType<Option<Uri>>(Assert.Single(command.Options, option => option.Name == "--endpoint"));

        Assert.Empty(parse.Errors);
        Assert.Equal(new Uri("http://127.0.0.1/"), parse.GetValue(endpoint));
    }

    [Theory]
    [InlineData("http://control.example:5181/")]
    [InlineData("https://127.0.0.1:5181/")]
    [InlineData("ftp://127.0.0.1:5181/")]
    [InlineData("http://127.0.0.1:5181/hubs/control/v1")]
    [InlineData("http://127.0.0.1:5181/?query=value")]
    [InlineData("http://127.0.0.1:5181/#fragment")]
    [InlineData("http://key@127.0.0.1:5181/")]
    [InlineData("not-an-origin")]
    public async Task ParserRejectsUnsafeOrMalformedOriginsBeforeAnyFixtureConnection(string origin)
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);

        var parse = ControlCommand.Create().Parse(["--endpoint", origin, "status"]);

        var error = Assert.Single(parse.Errors);
        Assert.Equal("Provide a path-free loopback HTTP or remote HTTPS control-service origin without credentials.",
            error.Message);
        Assert.Equal(0, fixture.ConnectionCount);
    }

    [Fact]
    public void ParserRejectsLegacyOriginOption()
    {
        var parse = ControlCommand.Create().Parse(["--control-origin", "http://127.0.0.1:5181/", "status"]);

        Assert.NotEmpty(parse.Errors);
        Assert.Contains(parse.Errors, error => error.Message.Contains("--control-origin", StringComparison.Ordinal));
    }
}
