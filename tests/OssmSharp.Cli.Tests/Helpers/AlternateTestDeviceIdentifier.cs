using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed record AlternateTestDeviceIdentifier : DeviceIdentifier
{
    private readonly string _providerKey;

    internal AlternateTestDeviceIdentifier(string id, string name, string providerKey)
    {
        Id = id;
        Name = name;
        _providerKey = providerKey;
    }

    public override string Id { get; }

    public override string Name { get; }
}
