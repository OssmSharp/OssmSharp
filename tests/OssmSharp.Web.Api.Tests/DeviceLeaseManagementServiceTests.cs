using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class DeviceLeaseManagementServiceTests : MockerTestBase<object>
{
    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        mocks.SetupOptions(new ControlServiceOptions { GuestControlCheckMilliseconds = 100 });
        var session = Mock.Get(mocks.GetOrCreateMock<IControlSession>().Instance);
        session.Setup(current => current.ShutdownAsync()).Returns(Task.CompletedTask);
    };

    protected override Func<Mocker, object> CreateComponentAction => mocks => new DeviceLeaseManagementService(
        mocks.GetOrCreateMock<IControlSession>().Instance,
        mocks.GetRequiredObject<IOptions<ControlServiceOptions>>(),
        TimeProvider.System,
        NullLogger<DeviceLeaseManagementService>.Instance);

    private new DeviceLeaseManagementService Component => (DeviceLeaseManagementService)base.Component;

    [Fact]
    public async Task StopAsyncShutsDownSessionOnce()
    {
        await Component.StopAsync(TestContext.Current.CancellationToken);

        Mocks.Verify<IControlSession>(current => current.ShutdownAsync(), TimesSpec.Once);
    }
}
