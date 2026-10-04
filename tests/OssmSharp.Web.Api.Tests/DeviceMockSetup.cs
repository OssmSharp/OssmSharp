using FastMoq;
using FastMoq.Providers.MoqProvider;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;

namespace OssmSharp.Web.Api.Tests;

internal static class DeviceMockSetup
{
    internal static Mock<IOssmDevice> ConnectedDevice(Mocker mocks)
    {
        var device = mocks.GetOrCreateMock<IOssmDevice>().AsMoq();
        device.SetupGet(device => device.IsConnected).Returns(true);
        device.SetupGet(device => device.IsArmed).Returns(true);
        device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(device => device.DisconnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(device => device.ResetPositionAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetResult(SetStatus.Completed, 5));
        device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceState?)null);
        device.Setup(device => device.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return device;
    }
}
