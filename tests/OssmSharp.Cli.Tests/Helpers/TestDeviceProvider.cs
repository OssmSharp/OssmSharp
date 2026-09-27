using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class TestDeviceProvider : IDeviceProvider
{
    private readonly Func<DeviceIdentifier, CancellationToken, Task<DeviceIdentityDetails>> _read;

    internal TestDeviceProvider(DeviceIdentityDetails result)
        : this((identifier, _) => Task.FromResult(result with { Identifier = identifier }))
    {
    }

    internal TestDeviceProvider(
        Func<DeviceIdentifier, CancellationToken, Task<DeviceIdentityDetails>> read)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    internal DeviceIdentifier? Identifier { get; private set; }

    internal CancellationToken LastCancellationToken { get; private set; }

    public Task<DeviceIdentityDetails> ReadInformationAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken)
    {
        Identifier = device;
        LastCancellationToken = cancellationToken;
        return _read(device, cancellationToken);
    }
}
