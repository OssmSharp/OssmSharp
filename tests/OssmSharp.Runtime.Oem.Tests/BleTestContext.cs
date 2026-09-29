using System.Buffers.Binary;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json.Nodes;
using FastMoq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Runtime.Oem;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem.Tests;

// Simulates the RAD BLE v1 surface used by OssmDevice: protocol info, correlated responses, leases, and the motion stream.
internal sealed class BleTestContext : IDisposable
{
    public const string InitialMotion = """{"homed":true,"positionMm":1.25,"speed":0,"stroke":50,"depth":10,"sensation":50,"buffer":100,"pattern":0,"targetPosition":0,"targetTimeMs":0,"strokeCount":0,"distanceMeters":0}""";
    public const byte StreamId = 7;
    private readonly Lock subscriptionSync = new();
    private readonly Dictionary<string, Subject<BleCharacteristicInfo>> subscribed = [];
    private readonly TaskCompletionSource connectionCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            connectionCancelled.TrySetResult();
            if (CompleteDisconnect)
            {
                Status.OnNext(ConnectionState.Disconnected);
            }
        });
        SetupRadProtocol();
    }

    public Mocker Mocks { get; } = new();

    public Mock<IBleManager> Manager { get; }

    public Mock<IPeripheral> Peripheral { get; }

    public FakeTimeProvider Clock { get; } = new();

    public BehaviorSubject<ConnectionState> Status { get; } = new(ConnectionState.Disconnected);

    public Subject<BleException> ConnectionFailures { get; } = new();

    public Subject<BleCharacteristicResult> Responses { get; } = new();

    public Subject<BleCharacteristicResult> Frames { get; } = new();

    public Subject<BleCharacteristicInfo> ResponseSubscriptionChanges { get; } = new();

    public Subject<BleCharacteristicInfo> StreamSubscriptionChanges { get; } = new();

    public Subject<ScanResult> ScanResults { get; } = new();

    public ScanConfig? ScanConfiguration { get; private set; }

    public ConnectionConfig? ConnectionConfiguration { get; private set; }

    public byte[]? ProtocolInfo { get; set; } = Encoding.UTF8.GetBytes("""{"protocol":"rad-ble","version":1,"maxMessageBytes":509,"streamHeaderBytes":20}""");

    public string Motion { get; set; } = InitialMotion;

    public uint Lease { get; set; } = 42;

    public byte MotionStreamId { get; set; } = StreamId;

    public List<JsonObject> Requests { get; } = [];

    // Returns a custom write observable, or null to complete the request normally.
    public Func<JsonObject, IObservable<BleCharacteristicResult>?>? Write { get; set; }

    public bool CompleteSubscription { get; set; } = true;

    public bool CompleteDisconnect { get; set; } = true;

    public int DisconnectCalls { get; private set; }

    public int NotificationDisposals { get; private set; }

    public int ScanDisposals { get; private set; }

    public Task ConnectionCancelled => connectionCancelled.Task;

    public static BleCharacteristicInfo Characteristic(string uuid) => new(new(BleProtocolConstants.Service), uuid, true, CharacteristicProperties.Read | CharacteristicProperties.Notify);

    public static BleCharacteristicResult WriteResult(byte[] data) => new(Characteristic(BleProtocolConstants.Request), BleCharacteristicEvent.Write, data);

    public static string Operation(JsonObject request) => request["op"]!.GetValue<string>();

    public OssmDevice CreateDevice() => new(Manager.Object, "device-1", Clock, NullLogger<DeviceBase>.Instance);

    public List<JsonObject> RequestsFor(string operation)
    {
        lock (Requests)
        {
            return [.. Requests.Where(request => Operation(request) == operation)];
        }
    }

    public void Complete(JsonObject request)
    {
        Respond(request, "accepted");
        Respond(request, "completed", result: DefaultResult(request));
    }

    public void Respond(JsonObject request, string stage, bool ok = true, string? code = null, JsonNode? result = null)
    {
        var response = new JsonObject
        {
            ["v"] = 1,
            ["id"] = request["id"]!.GetValue<uint>(),
            ["stage"] = stage,
            ["ok"] = ok,
        };
        if (code is not null)
        {
            response["code"] = code;
        }

        if (result is not null)
        {
            response["result"] = result;
        }

        SendResponse(Encoding.UTF8.GetBytes(response.ToJsonString()));
    }

    public void SendResponse(byte[]? data) => Responses.OnNext(new(Characteristic(BleProtocolConstants.Response), BleCharacteristicEvent.Notification, data));

    public void Publish(string payload, byte streamId = StreamId, ushort flags = 1, byte version = 1, int? declaredLength = null)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[20 + body.Length];
        frame[0] = version;
        frame[1] = streamId;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)(declaredLength ?? body.Length));
        body.CopyTo(frame, 20);
        PublishFrame(frame);
    }

    public void PublishFrame(byte[]? frame) => Frames.OnNext(new(Characteristic(BleProtocolConstants.SensorStream), BleCharacteristicEvent.Notification, frame));

    // A live read needs a frame after it registers, so frames are streamed until the operation completes.
    public async Task StreamUntilAsync(Task operation, CancellationToken cancellationToken)
    {
        while (!operation.IsCompleted)
        {
            if (RequestsFor("stream.start").Count > RequestsFor("stream.stop").Count)
            {
                Publish(Motion, streamId: MotionStreamId);
            }

            await Task.WhenAny(operation, Task.Delay(5, cancellationToken));
        }

        await operation;
    }

    public async Task<T> StreamUntilAsync<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        await StreamUntilAsync((Task)operation, cancellationToken);
        return await operation;
    }

    public async Task<OssmDevice> ConnectAsync(CancellationToken cancellationToken)
    {
        var device = CreateDevice();
        await StreamUntilAsync(device.ConnectAsync(cancellationToken), cancellationToken);
        return device;
    }

    public void AcknowledgeSubscriptions()
    {
        lock (subscriptionSync)
        {
            CompleteSubscription = true;
            foreach (var (uuid, changes) in subscribed)
            {
                changes.OnNext(Characteristic(uuid));
            }
        }
    }

    public void Dispose()
    {
        Status.Dispose();
        ConnectionFailures.Dispose();
        Responses.Dispose();
        Frames.Dispose();
        ResponseSubscriptionChanges.Dispose();
        StreamSubscriptionChanges.Dispose();
        ScanResults.Dispose();
    }

    private void SetupRadProtocol()
    {
        Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo))
            .Returns(() => Observable.Return(new BleCharacteristicResult(Characteristic(BleProtocolConstants.ProtocolInfo), BleCharacteristicEvent.Read, ProtocolInfo)));
        Peripheral.Setup(peripheral => peripheral.WhenCharacteristicSubscriptionChanged(BleProtocolConstants.Service, BleProtocolConstants.Response))
            .Returns(ResponseSubscriptionChanges);
        Peripheral.Setup(peripheral => peripheral.WhenCharacteristicSubscriptionChanged(BleProtocolConstants.Service, BleProtocolConstants.SensorStream))
            .Returns(StreamSubscriptionChanges);
        Peripheral.Setup(peripheral => peripheral.NotifyCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.Response, It.IsAny<bool>()))
            .Returns(Notify(Responses, ResponseSubscriptionChanges, BleProtocolConstants.Response));
        Peripheral.Setup(peripheral => peripheral.NotifyCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.SensorStream, It.IsAny<bool>()))
            .Returns(Notify(Frames, StreamSubscriptionChanges, BleProtocolConstants.SensorStream));
        Peripheral.Setup(peripheral => peripheral.WriteCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.Request, It.IsAny<byte[]>(), true))
            .Returns((string service, string characteristic, byte[] data, bool withResponse) => Observable.Defer(() =>
            {
                var request = JsonNode.Parse(data)!.AsObject();
                lock (Requests)
                {
                    Requests.Add(request);
                }

                if (Write?.Invoke(request) is { } custom)
                {
                    return custom;
                }

                Complete(request);
                return Observable.Return(WriteResult(data));
            }));
    }

    private JsonNode DefaultResult(JsonObject request) => Operation(request) switch
    {
        "control.acquire" => new JsonObject { ["lease"] = Lease, ["ttlMs"] = 10000 },
        "control.renew" => new JsonObject { ["ttlMs"] = 10000 },
        "stream.start" => new JsonObject
        {
            ["streamId"] = MotionStreamId,
            ["path"] = "motion.speed",
            ["surface"] = "motion",
            ["rateHz"] = 10,
            ["batchSize"] = 1,
            ["encoding"] = "json-v1",
        },
        "stream.stop" => new JsonObject { ["streamId"] = StreamId, ["stopped"] = true },
        "setting.write" => new JsonObject { ["path"] = request["path"]!.DeepClone(), ["value"] = request["args"]!["value"]!.DeepClone() },
        _ => new JsonObject(),
    };

    private IObservable<BleCharacteristicResult> Notify(Subject<BleCharacteristicResult> source, Subject<BleCharacteristicInfo> changes, string uuid)
        => Observable.Create<BleCharacteristicResult>(observer =>
        {
            var subscription = source.Subscribe(observer);
            lock (subscriptionSync)
            {
                subscribed[uuid] = changes;
                if (CompleteSubscription)
                {
                    changes.OnNext(Characteristic(uuid));
                }
            }

            return Disposable.Create(() =>
            {
                subscription.Dispose();
                lock (subscriptionSync)
                {
                    subscribed.Remove(uuid);
                    NotificationDisposals++;
                }

                changes.OnNext(Characteristic(uuid) with { IsNotifying = false });
            });
        });
}
