using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using PrettyPrompt;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PrettyPromptInputComponentTests : MockerTestBase<object>, IAsyncDisposable
{
    private StubPrompt prompt = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        prompt = new StubPrompt(new PromptResult(true, string.Empty, default));
        mocks.AddType<IPrompt>(prompt);
    };

    protected override Func<Mocker, object> CreateComponentAction => mocks =>
        new PrettyPromptInput(mocks.GetRequiredObject<IPrompt>());

    private new PrettyPromptInput Component => (PrettyPromptInput)base.Component;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            await prompt.DisposeAsync();
            base.Dispose();
        }
    }

    [Theory]
    [InlineData(true, "status", false)]
    [InlineData(false, "", true)]
    public async Task MapsPromptResult(bool isSuccess, string text, bool isInterrupted)
    {
        prompt.Result = new PromptResult(isSuccess, text, default);

        var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().Be(text);
        result.IsInterrupted.Should().Be(isInterrupted);
    }

    [Fact]
    public async Task DisposesOwnedPrompt()
    {
        await Component.DisposeAsync();

        prompt.IsDisposed.Should().BeTrue();
    }

    private sealed class StubPrompt(PromptResult result) : IPrompt, IAsyncDisposable
    {
        internal PromptResult Result { get; set; } = result;

        internal bool IsDisposed { get; private set; }

        public Task<PromptResult> ReadLineAsync() => Task.FromResult(Result);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
