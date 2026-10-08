using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using OssmSharp.Web;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class WebApplicationsTests : MockerTestBase<WebApplications>, IAsyncDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    protected override Action<Mocker> SetupMocksAction => mocks =>
        mocks.AddType<IOptions<HostOptions>>(Options.Create(new HostOptions()));

    [Fact]
    public async Task StartsApiFirstAndStopsAndDisposesInReverseOrder()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(actions, started, stopping: TestContext.Current.CancellationToken);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Component.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["start0", "start1", "start2", "stop2", "dispose2", "stop1", "dispose1", "stop0", "dispose0"],
            actions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartupFailureStopsAndDisposesEveryCreatedApplication(int failedIndex)
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(actions, started, failedIndex, stopping: TestContext.Current.CancellationToken);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ExecuteTask!);

        Assert.Equal(failedIndex + 1, actions.Count(action => action.StartsWith("dispose", StringComparison.Ordinal)));
        Assert.Equal(failedIndex + 1, actions.Count(action => action.StartsWith("stop", StringComparison.Ordinal)));
        Assert.Equal("dispose0", actions[^1]);
        Assert.DoesNotContain($"start{failedIndex + 1}", actions);
    }

    [Fact]
    public async Task AnApplicationStoppingRequestsProcessShutdown()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        ArrangeApplications(actions, started, stopping: stopping.Token);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await stopping.CancelAsync();

        Mocks.Verify<IHostApplicationLifetime>(lifetime => lifetime.StopApplication(), TimesSpec.Once);
        await Component.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FactoryFailureReleasesApplicationsAlreadyStarted()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(actions, started, stopping: TestContext.Current.CancellationToken);
        Mocks.GetOrCreateMock<IWebApplicationFactory>().AsMoq().Setup(factory => factory.Create(1))
            .Throws(new InvalidOperationException("Test build failure"));

        await Component.StartAsync(TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ExecuteTask!);

        Assert.Equal("Test build failure", failure.Message);
        Assert.Equal(["start0", "stop0", "dispose0"], actions);
    }

    [Fact]
    public async Task StartupAndCleanupFailuresArePreservedAndAllApplicationsAreReleased()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(
            actions,
            started,
            failedIndex: 2,
            shutdownFailedIndex: 2,
            disposeFailedIndex: 1,
            stopping: TestContext.Current.CancellationToken);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Component.ExecuteTask!);

        Assert.Equal(
            ["Test disposal failure", "Test startup failure", "Test stop failure"],
            failure.Flatten().InnerExceptions.Select(exception => exception.Message).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["start0", "start1", "start2", "stop2", "dispose2", "stop1", "dispose1", "stop0", "dispose0"],
            actions);
    }

    [Fact]
    public async Task ShutdownFailureStillDisposesAndStopsTheRemainingApplications()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(actions, started, shutdownFailedIndex: 2, stopping: TestContext.Current.CancellationToken);

        await Component.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Component.StopAsync(TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ExecuteTask!);

        Assert.Equal("Test stop failure", failure.Message);
        Assert.Equal(
            ["start0", "start1", "start2", "stop2", "dispose2", "stop1", "dispose1", "stop0", "dispose0"],
            actions);
    }

    [Fact]
    public async Task ShutdownWaitsForCleanupEvenWhenTheCallerStopsWaiting()
    {
        var actions = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArrangeApplications(actions, started, stopping: TestContext.Current.CancellationToken);
        var last = Mocks.GetRequiredObject<IWebApplicationFactory>().Create(2);
        Mock.Get(last).Setup(host => host.StopAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) =>
            {
                Assert.False(token.IsCancellationRequested);
                actions.Add("stop2");
                stopping.TrySetResult();
                return released.Task;
            });

        await Component.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var cleanup = Component.StopAsync(caller.Token);
        await stopping.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(cleanup.IsCompleted);
        }
        finally
        {
            released.TrySetResult();
        }
        await cleanup.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["start0", "start1", "start2", "stop2", "dispose2", "stop1", "dispose1", "stop0", "dispose0"],
            actions);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var provider in _providers)
                await provider.DisposeAsync();
        }
        finally
        {
            base.Dispose();
        }
    }

    private void ArrangeApplications(
        List<string> actions,
        TaskCompletionSource started,
        int? failedIndex = null,
        int? shutdownFailedIndex = null,
        int? disposeFailedIndex = null,
        CancellationToken stopping = default)
    {
        for (var index = 0; index < 3; index++)
        {
            var current = index;
            var host = Mocks.CreateStandaloneFastMock<IHost>().AsMoq();
            var lifetime = Mocks.CreateStandaloneFastMock<IHostApplicationLifetime>().AsMoq();
            lifetime.SetupGet(value => value.ApplicationStopping).Returns(current == 0 ? stopping : default);
            var services = new ServiceCollection().AddSingleton(lifetime.Object).BuildServiceProvider();
            _providers.Add(services);
            host.SetupGet(value => value.Services).Returns(services);
            host.Setup(value => value.StartAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                actions.Add($"start{current}");
                if (failedIndex == current)
                    return Task.FromException(new InvalidOperationException("Test startup failure"));
                if (current == 2)
                    started.TrySetResult();
                return Task.CompletedTask;
            });
            host.Setup(value => value.StopAsync(It.IsAny<CancellationToken>())).Returns(() =>
            {
                actions.Add($"stop{current}");
                if (shutdownFailedIndex == current)
                    return Task.FromException(new InvalidOperationException("Test stop failure"));
                return Task.CompletedTask;
            });
            host.Setup(value => value.Dispose()).Callback(() =>
            {
                actions.Add($"dispose{current}");
                services.Dispose();
                if (disposeFailedIndex == current)
                    throw new InvalidOperationException("Test disposal failure");
            });
            Mocks.GetOrCreateMock<IWebApplicationFactory>().AsMoq()
                .Setup(factory => factory.Create(current)).Returns(host.Object);
        }
    }
}
