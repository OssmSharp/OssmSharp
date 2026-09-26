using System.Threading.Channels;

namespace OssmSharp.Device.Stock;

internal sealed record StockGattProfile(
    string PeripheralId,
    Guid Service,
    IReadOnlyCollection<Guid> Characteristics,
    byte[] Identity,
    byte[] Patterns,
    ChannelReader<byte[]> Notifications);
