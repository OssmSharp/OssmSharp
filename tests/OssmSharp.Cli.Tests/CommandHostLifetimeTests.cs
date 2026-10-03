using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class CommandHostLifetimeTests : MockerTestBase<object>
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks.AddType<PromptInterruptState>(new PromptInterruptState());

    protected override Func<Mocker, object> CreateComponentAction => mocks => new CommandHostLifetime(
        mocks.GetOrCreateMock<IHostApplicationLifetime>().Instance,
        mocks.GetRequiredObject<PromptInterruptState>());

    private new CommandHostLifetime Component => (CommandHostLifetime)base.Component;

    [Fact]
    public void InterruptStopsApplicationOutsideCommandExecution()
    {
        using var lifetime = Component;

        lifetime.Interrupt();

        Mocks.Verify<IHostApplicationLifetime>(application => application.StopApplication(), TimesSpec.Once);
    }

    [Fact]
    public void InterruptLeavesActiveCommandCancellationToPrompt()
    {
        var interruptState = Mocks.GetRequiredObject<PromptInterruptState>();
        using var lifetime = Component;
        interruptState.BeginCommand();

        lifetime.Interrupt();

        Mocks.Verify<IHostApplicationLifetime>(application => application.StopApplication(), TimesSpec.NeverCalled);

        interruptState.EndCommand();
        lifetime.Interrupt();

        Mocks.Verify<IHostApplicationLifetime>(application => application.StopApplication(), TimesSpec.Once);
    }

    [Fact]
    public void InterruptStopsApplicationAfterTrackedCommandCompletes()
    {
        var interruptState = Mocks.GetRequiredObject<PromptInterruptState>();
        using var lifetime = Component;
        interruptState.BeginCommand();
        interruptState.TrackCommand(Task.CompletedTask);

        lifetime.Interrupt();

        Mocks.Verify<IHostApplicationLifetime>(application => application.StopApplication(), TimesSpec.Once);
    }
}
