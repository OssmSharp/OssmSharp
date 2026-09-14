using OSSMDotNet.Web.PrototypeCli;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

public sealed class AdminCommandTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    public void ValidLocalCommandParsesWithoutExecuting(string operation)
    {
        var result = AdminCommand.Create().Parse([operation, "--admin-origin", "http://127.0.0.1:5181"]);

        Assert.Empty(result.Errors);
        Assert.Equal(operation, result.CommandResult.Command.Name);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("not-an-origin")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://127.0.0.1:5181/path")]
    [InlineData("http://127.0.0.1:5181/?secret=token")]
    [InlineData("http://127.0.0.1:5181/#operator=secret")]
    [InlineData("http://user@127.0.0.1:5181")]
    public void InvalidOriginReportsAParseErrorInsteadOfCrashing(string origin)
    {
        var result = AdminCommand.Create().Parse(["status", "--admin-origin", origin]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void CommandsCannotOmitTheOriginOrUseTheOldProcessLauncher()
    {
        Assert.NotEmpty(AdminCommand.Create().Parse(["status"]).Errors);
        Assert.NotEmpty(AdminCommand.Create().Parse(["start"]).Errors);
    }
}
