using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class CommandHostLifetimeTests
{
    [Fact]
    public void InterruptStopsApplicationOutsideCommandExecution()
    {
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        using var lifetime = new CommandHostLifetime(applicationLifetime.Object, new PromptInterruptState());

        lifetime.Interrupt();

        applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Once);
    }

    [Fact]
    public void InterruptLeavesActiveCommandCancellationToPrompt()
    {
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        var interruptState = new PromptInterruptState();
        using var lifetime = new CommandHostLifetime(applicationLifetime.Object, interruptState);
        interruptState.BeginCommand();

        lifetime.Interrupt();

        applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Never);

        interruptState.EndCommand();
        lifetime.Interrupt();

        applicationLifetime.Verify(lifetime => lifetime.StopApplication(), Times.Once);
    }

    [Fact]
    public void InterruptStopsApplicationAfterTrackedCommandCompletes()
    {
        var applicationLifetime = new Mock<IHostApplicationLifetime>();
        var interruptState = new PromptInterruptState();
        using var lifetime = new CommandHostLifetime(applicationLifetime.Object, interruptState);
        interruptState.BeginCommand();
        interruptState.TrackCommand(Task.CompletedTask);

        lifetime.Interrupt();

        applicationLifetime.Verify(application => application.StopApplication(), Times.Once);
    }
}