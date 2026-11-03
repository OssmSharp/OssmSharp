using System.Reactive.Disposables;
using System.Text.Json.Nodes;
using FastMoq.Providers.MoqProvider;
using Moq;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public class OssmDeviceGeneratedCase : OssmDeviceTestBase, IAsyncDisposable
{
    private int caseCreated;
    private int disposed;

    public OssmDevice Device => Component;
    internal BleTestContext Ble => context;

    // The xUnit-owned instance is the first fresh case, not a decorative mock container.
    // Every later generated case and shrink owns another independent base fixture.
    protected OssmDeviceGeneratedCase CreateCase() =>
        Interlocked.Exchange(ref caseCreated, 1) == 0 ? this : new OssmDeviceGeneratedCase();

    public Task<OssmDevice> ConnectAsync() => context.ConnectAsync(Component, TestContext.Current.CancellationToken);

    internal List<(string Path, int Value)> Requests(bool allowRetries = false)
    {
        var requests = context.RequestsFor("setting.write", "motion.");
        foreach (var request in requests)
        {
            Assert.Equal(1, request["v"]!.GetValue<int>());
            Assert.Equal("setting.write", request["op"]!.GetValue<string>());
            Assert.Equal(42u, request["lease"]!.GetValue<uint>());
            Assert.NotEqual(0u, request["id"]!.GetValue<uint>());
        }
        var byId = requests.GroupBy(request => request["id"]!.GetValue<uint>()).ToArray();
        if (!allowRetries)
            Assert.Equal(requests.Count, byId.Length);
        else
        {
            foreach (var group in byId)
                Assert.All(group, request => Assert.Equal(group.First().ToJsonString(), request.ToJsonString()));
        }
        return requests.Select(request => (request["path"]!.GetValue<string>(), request["args"]!["value"]!.GetValue<int>())).ToList();
    }

    public void Publish(DeviceState state, byte streamId = BleTestContext.StreamId) => context.Publish(new JsonObject
    {
        ["pattern"] = (int)state.Pattern,
        ["speed"] = state.CurrentDeviceVelocityPercent,
        ["stroke"] = state.Stroke,
        ["depth"] = state.Depth,
        ["sensation"] = state.Sensation,
    }.ToJsonString(), streamId);

    public List<IObserver<BleException>> CaptureFailureObservers()
    {
        var observers = new List<IObserver<BleException>>();
        var failures = Mocks.GetOrCreateMock<IObservable<BleException>>().AsMoq();
        failures.Setup(source => source.Subscribe(It.IsAny<IObserver<BleException>>()))
            .Returns((IObserver<BleException> observer) =>
            {
                observers.Add(observer);
                return Disposable.Empty;
            });
        context.Peripheral.Setup(peripheral => peripheral.WhenConnectionFailed()).Returns(failures.Object);
        return observers;
    }

    public new async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            await base.DisposeAsync();
    }
}
