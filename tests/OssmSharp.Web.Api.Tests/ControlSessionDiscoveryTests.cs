using FastMoq;
using FastMoq.Extensions;
using Moq;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlSessionDiscoveryTests : MockerTestBase<ControlSession>, IAsyncDisposable
{
    private FakeOssmDevice _device = null!;
    private List<string> _identifiers = null!;
    private TaskCompletionSource<DiscoveredDevice?> _detected = null!;
    private CancellationToken _scanToken;
    private int _scans;
    private bool _honorCancellation = true;
    private bool _disposed;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _device = new();
        _identifiers = [];
        _detected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mocks.AddType<Func<string, IOssmDevice>>(identifier =>
        {
            _identifiers.Add(identifier);
            return _device;
        });
        mocks.SetupOptions(new ControlServiceOptions());
        var discovery = Mock.Get(mocks.GetOrCreateMock<IDeviceDiscovery>().Instance);
        discovery.Setup(current => current.FindFirstAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) =>
            {
                _scans++;
                _scanToken = token;
                return _honorCancellation ? _detected.Task.WaitAsync(token) : _detected.Task;
            });
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detected.TrySetResult(null);
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            if (_identifiers.Count == 0)
            {
                await _device.DisposeAsync();
            }

            base.Dispose();
        }
    }

    [Fact]
    public async Task StateBeforeConnectDoesNotDiscoverOrCreateDevice()
    {
        var state = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _scans);
        Assert.Empty(_identifiers);
        Assert.NotNull(state.Device);
        Assert.False(state.Device.IsArmed);
        Assert.Equal(0, _device.Disposals);
    }

    [Fact]
    public async Task ConnectDiscoversBeforeConstructionAndRetainsDeviceForReconnect()
    {
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, _scans);
        Assert.Empty(_identifiers);
        Assert.Equal(0, _device.Connects);

        _detected.SetResult(new("device-1", "OSSM"));
        Assert.Equal(new ControlResponse("connect-1", "connected", true), await connect);
        await _device.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.True((await Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken)).Accepted);

        Assert.Equal(["device-1"], _identifiers);
        Assert.Equal(1, _scans);
        Assert.Equal(2, _device.Connects);
        await DisposeAsync();
        Assert.Equal(1, _device.Disposals);
    }

    [Fact]
    public async Task ConcurrentConnectionsShareDiscoveryAndConstruction()
    {
        var first = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        var second = Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken);

        Assert.Equal(1, _scans);
        Assert.Empty(_identifiers);
        _detected.SetResult(new("device-1", "OSSM"));
        Assert.True((await first).Accepted);
        Assert.True((await second).Accepted);
        Assert.Equal(["device-1"], _identifiers);
    }

    [Fact]
    public async Task EmptyDiscoveryCreatesNothingAndNextConnectRetries()
    {
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        _detected.SetResult(null);

        Assert.Equal(new ControlResponse("connect-1", "device-not-ready", false), await connect);
        Assert.Empty(_identifiers);
        Assert.Equal(0, _device.Connects);

        _detected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken);
        _detected.SetResult(new("device-2", "OSSM"));
        Assert.True((await retry).Accepted);
        Assert.Equal(["device-2"], _identifiers);
        Assert.Equal(2, _scans);
    }

    [Fact]
    public async Task CallerCancellationDoesNotConstructDevice()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = Component.ConnectAsync("connect-1", cancellation.Token);

        await cancellation.CancelAsync();

        Assert.Equal(new ControlResponse("connect-1", "cancelled", false), await connect);
        Assert.True(_scanToken.IsCancellationRequested);
        Assert.Empty(_identifiers);
        Assert.Equal(0, _device.Connects);
        _detected.TrySetResult(null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopOrShutdownWaitsForDiscoveryAndRejectsLateDevice(bool shutdown, bool found)
    {
        _honorCancellation = false;
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        var stop = shutdown ? Component.ShutdownAsync() : Component.StopAsync("stop-1");
        try
        {
            Assert.True(_scanToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.Empty(_identifiers);
        }
        finally
        {
            _detected.TrySetResult(found ? new("device-1", "OSSM") : null);
            await stop;
            Assert.Equal(new ControlResponse("connect-1", "cancelled", false), await connect);
        }

        Assert.Empty(_identifiers);
        Assert.Equal(0, _device.Connects);
        Assert.Equal(0, _device.Stops);
    }

    [Fact]
    public async Task ConnectAfterShutdownDoesNotDiscoverOrConstructDevice()
    {
        await Component.ShutdownAsync();

        var response = await Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);

        Assert.Equal(new ControlResponse("connect-1", "cancelled", false), response);
        Assert.Equal(0, _scans);
        Assert.Empty(_identifiers);
    }

    [Fact]
    public async Task InvalidCorrelationDoesNotDiscover()
    {
        var result = await Component.ConnectAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(new ControlResponse(string.Empty, "invalid-correlation", false), result);
        Assert.Equal(0, _scans);
        Assert.Empty(_identifiers);
    }
}
