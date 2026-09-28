using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using FastMoq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OssmSharp.Device.Runtime;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Oem.Tests;

internal sealed class BleTestContext : IDisposable
{
    public const string InitialState = "{\"timestamp\":123,\"state\":\"menu\",\"pattern\":0,\"speed\":0,\"stroke\":50,\"depth\":10,\"sensation\":50,\"buffer\":100,\"position\":1.25,\"sessionId\":\"\"}";

    public BleTestContext()
    {
        Manager = Mock.Get(Mocks.GetOrCreateMock<IBleManager>().Instance);
        Peripheral = Mock.Get(Mocks.GetOrCreateMock<IPeripheral>().Instance);
        Manager.Setup(manager => manager.RequestAccess()).Returns(Observable.Return(AccessState.Available));
        Manager.Setup(manager => manager.GetKnownPeripheral("device-1")).Returns(Peripheral.Object);
        Manager.Setup(manager => manager.Scan(It.IsAny<ScanConfig>())).Returns((ScanConfig config) =>
        {
            ScanConfiguration = config;
            return ScanResults.Finally(() => ScanDisposals++);
        });
        Peripheral.SetupGet(peripheral => peripheral.Uuid).Returns("device-1");
        Peripheral.SetupGet(peripheral => peripheral.Name).Returns("OSSM");
        Peripheral.SetupGet(peripheral => peripheral.Status).Returns(() => Status.Value);
        Peripheral.Setup(peripheral => peripheral.WhenStatusChanged()).Returns(Status);
        Peripheral.Setup(peripheral => peripheral.WhenConnectionFailed()).Returns(ConnectionFailures);
        Peripheral.Setup(peripheral => peripheral.Connect(It.IsAny<ConnectionConfig>())).Callback((ConnectionConfig config) =>
        {
            ConnectionConfiguration = config;
            Status.OnNext(ConnectionState.Connected);
        });
        Peripheral.Setup(peripheral => peripheral.CancelConnection()).Callback(() =>
        {
            DisconnectCalls++;
            if (CompleteDisconnect)
            {
                Status.OnNext(ConnectionState.Disconnected);
            }
        });
        Peripheral.Setup(peripheral => peripheral.WhenCharacteristicSubscriptionChanged(BleProtocol.Service, BleProtocol.State))
            .Returns(SubscriptionChanges);
        Peripheral.Setup(peripheral => peripheral.NotifyCharacteristic(BleProtocol.Service, BleProtocol.State, false))
            .Returns(Observable.Create<BleCharacteristicResult>(observer =>
            {
                var subscription = Notifications.Subscribe(observer);
                if (CompleteSubscription)
                {
                    SubscriptionChanges.OnNext(StateCharacteristic);
                }

                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    NotificationDisposals++;
                    SubscriptionChanges.OnNext(StateCharacteristic with { IsNotifying = false });
                });
            }));
        Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocol.Service, BleProtocol.State))
            .Returns(() =>
            {
                Reads++;
                return Read?.Invoke() ?? Observable.Return(StateResult(StateData));
            });
        Peripheral.Setup(peripheral => peripheral.WriteCharacteristic(BleProtocol.Service, BleProtocol.Command, It.IsAny<byte[]>(), true))
            .Returns((string service, string characteristic, byte[] data, bool withResponse) =>
            {
                Writes.Add(Encoding.UTF8.GetString(data));
                return Write?.Invoke(data) ?? Observable.Return(new BleCharacteristicResult(
                    new(new(BleProtocol.Service), BleProtocol.Command, false, CharacteristicProperties.Write), BleCharacteristicEvent.Write, data));
            });
    }

    public static BleCharacteristicInfo StateCharacteristic { get; } = new(new(BleProtocol.Service), BleProtocol.State, true, CharacteristicProperties.Read | CharacteristicProperties.Notify);

    public Mocker Mocks { get; } = new();

    public Mock<IBleManager> Manager { get; }

    public Mock<IPeripheral> Peripheral { get; }

    public BehaviorSubject<ConnectionState> Status { get; } = new(ConnectionState.Disconnected);

    public Subject<BleException> ConnectionFailures { get; } = new();

    public Subject<BleCharacteristicResult> Notifications { get; } = new();

    public Subject<BleCharacteristicInfo> SubscriptionChanges { get; } = new();

    public Subject<ScanResult> ScanResults { get; } = new();

    public ScanConfig? ScanConfiguration { get; private set; }

    public ConnectionConfig? ConnectionConfiguration { get; private set; }

    public byte[]? StateData { get; set; } = Encoding.UTF8.GetBytes(InitialState);

    public List<string> Writes { get; } = [];

    public Func<IObservable<BleCharacteristicResult>>? Read { get; set; }

    public Func<byte[], IObservable<BleCharacteristicResult>>? Write { get; set; }

    public bool CompleteSubscription { get; set; } = true;

    public bool CompleteDisconnect { get; set; } = true;

    public int Reads { get; private set; }

    public int DisconnectCalls { get; private set; }

    public int NotificationDisposals { get; private set; }

    public int ScanDisposals { get; private set; }

    public static BleCharacteristicResult StateResult(byte[]? data) => new(StateCharacteristic, BleCharacteristicEvent.Read, data);

    public OssmDevice CreateDevice() => new(Manager.Object, "device-1", NullLogger<DeviceBase>.Instance);

    public void Dispose()
    {
        Status.Dispose();
        ConnectionFailures.Dispose();
        Notifications.Dispose();
        SubscriptionChanges.Dispose();
        ScanResults.Dispose();
    }
}
