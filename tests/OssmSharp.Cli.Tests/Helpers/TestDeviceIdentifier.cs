using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed record TestDeviceIdentifier : DeviceIdentifier
{
    private readonly object _providerState;

    internal TestDeviceIdentifier(string id, string name, object providerState)
    {
        Id = id;
        Name = name;
        _providerState = providerState;
    }

    public override string Id { get; }

    public override string Name { get; }
}
