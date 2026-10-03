using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Moq;
using PrettyPrompt;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PrettyPromptInputFactoryTests : MockerTestBase<object>, IAsyncDisposable
{
    private int constructions;

    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks
        .GetOrCreateMock<IPrompt>()
        .AsMoq()
        .Setup(prompt => prompt.ReadLineAsync())
        .ReturnsAsync(new PromptResult(true, string.Empty, default));

    protected override Func<Mocker, object> CreateComponentAction => mocks => new PrettyPromptInput(() =>
    {
        constructions++;
        return mocks.GetOrCreateMock<IPrompt>().Instance;
    });

    private new PrettyPromptInput Component => (PrettyPromptInput)base.Component;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            base.Dispose();
        }
    }

    [Fact]
    public async Task ConstructsPromptOnFirstRead()
    {
        constructions.Should().Be(0);

        await Component.ReadAsync(TestContext.Current.CancellationToken);

        constructions.Should().Be(1);
        Mocks.Verify<IPrompt>(prompt => prompt.ReadLineAsync(), TimesSpec.Once);
    }
}
