using FastMoq;
using FastMoq.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class DeviceLeaseManagementServiceTests
{
    [Fact]
    public async Task StopAsyncShutsDownSessionOnce()
    {
        using var mocks = new Mocker();
        mocks.SetupOptions(new ControlServiceOptions { GuestControlCheckMilliseconds = 100 });
        mocks.AddType<ILogger<DeviceLeaseManagementService>>(NullLogger<DeviceLeaseManagementService>.Instance);
        var session = Mock.Get(mocks.GetOrCreateMock<IControlSession>().Instance);
        session.Setup(current => current.ShutdownAsync()).Returns(Task.CompletedTask);
        var service = mocks.CreateInstance<DeviceLeaseManagementService>() ??
            throw new InvalidOperationException("The hosted service was not created.");

        await service.StopAsync(TestContext.Current.CancellationToken);

        session.Verify(current => current.ShutdownAsync(), Times.Once);
    }
}
