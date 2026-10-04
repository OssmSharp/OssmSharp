using System.Reactive.Linq;
using System.Reactive.Subjects;
using FastMoq;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using Shiny.BluetoothLE;

namespace OssmSharp.Cli.Tests;

internal sealed class CommandTestContext : IDisposable
{
    private readonly bool ownsMocks;

    public CommandTestContext(Mocker? mocks = null)
    {
        ownsMocks = mocks is null;
        Mocks = mocks ?? new Mocker();
        Manager = Mocks.GetOrCreateMock<IBleManager>().AsMoq();
        Manager.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        Manager.Setup(manager => manager.Scan(It.IsAny<ScanConfig>()))
            .Returns(() =>
            {
                Scans++;
                return ScanResults.Finally(() => ScanDisposals++);
            });
        Discovery = new DeviceDiscovery(Manager.Object, Clock);
    }

    public Mocker Mocks { get; }

    public Mock<IBleManager> Manager { get; }

    public FakeTimeProvider Clock { get; } = new();

    public Subject<ScanResult> ScanResults { get; } = new();

    public IDeviceDiscovery Discovery { get; }

    public int Scans { get; private set; }

    public int ScanDisposals { get; private set; }

    public void Advertise(string identifier, string? name)
    {
        var peripheral = Mocks.CreateStandaloneFastMock<IPeripheral>().AsMoq();
        peripheral.SetupGet(device => device.Uuid).Returns(identifier);
        peripheral.SetupGet(device => device.Name).Returns(name);
        ScanResults.OnNext(new(peripheral.Object, -40, Mocks.GetOrCreateMock<IAdvertisementData>().Instance));
    }

    public void Dispose()
    {
        try
        {
            ScanResults.Dispose();
        }
        finally
        {
            if (ownsMocks)
            {
                Mocks.Dispose();
            }
        }
    }
}
