namespace OSSMDotNet.DeviceBridge.IntegrationTests;

public sealed class DeviceBridgeContractTests
{
    [Fact]
    public void OperationsReturnBridgeGeneratedSequenceNumbers()
    {
        var setMotion = typeof(IDeviceBridge).GetMethod(nameof(IDeviceBridge.SetMotionAsync));
        var stop = typeof(IDeviceBridge).GetMethod(nameof(IDeviceBridge.StopAsync));

        Assert.NotNull(setMotion);
        Assert.Equal(typeof(Task<long>), setMotion.ReturnType);
        Assert.Equal(
            [typeof(MotionSettings), typeof(CancellationToken)],
            setMotion.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(stop);
        Assert.Equal(typeof(Task<long>), stop.ReturnType);
        Assert.Equal(
            [typeof(CancellationToken)],
            stop.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(long?), typeof(DeviceState).GetProperty("SequenceNumber")?.PropertyType);
        Assert.Null(typeof(DeviceState).GetProperty("CommandId"));
    }
}
