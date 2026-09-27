namespace OssmSharp.Device.Stock;

internal interface IDeviceTransport
{
    Task<IDeviceConnection> ConnectAsync(DeviceIdentifier selected, Guid service, CancellationToken cancellationToken);
}
