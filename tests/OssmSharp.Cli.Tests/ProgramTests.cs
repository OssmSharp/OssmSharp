using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task MainReturnsFailureEvenWhenDiagnosticsAreUnavailable()
    {
        using var output = new StringWriter();
        using var error = new FailingWriter(_ => true);

        (await Program.InvokeAsync(["--unknown"], output, error)).Should().Be(1);
        output.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "--help" }, "Control an OSSM device from the command line.")]
    [InlineData(new[] { "list", "--help" }, "List nearby devices and their identifiers.")]
    [InlineData(new[] { "prompt", "--help" }, "Open an interactive device prompt.")]
    public async Task MainHelpReturnsZeroWithoutDeviceAccess(string[] args, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(args, output, error)).Should().Be(0);

        output.ToString().Should().Contain(expected);
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task MainVersionReturnsZeroWithoutDeviceAccess()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(["--version"], output, error)).Should().Be(0);

        output.ToString().Trim().Should().MatchRegex(@"^\d+\.\d+\.\d+[^\r\n]*$");
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "unknown" }, "unknown")]
    [InlineData(new[] { "--unknown" }, "--unknown")]
    [InlineData(new[] { "list", "--unknown" }, "--unknown")]
    [InlineData(new[] { "prompt", "--device" }, "--device")]
    [InlineData(new[] { "prompt", "--device", "" }, "--device requires <identifier>")]
    [InlineData(new[] { "prompt", "--device", " " }, "--device requires <identifier>")]
    public async Task MainInvalidArgumentsReturnTwoWithoutDeviceAccess(string[] args, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await Program.InvokeAsync(args, output, error)).Should().Be(2);

        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("ERROR input:").And.Contain("--help").And.Contain(expected)
            .And.NotContain("Scanning").And.NotContain("Connecting");
    }
}
