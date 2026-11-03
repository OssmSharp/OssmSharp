using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using OssmSharp.Runtime.Oem.Tests.Models;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

internal sealed class PendingDeviceRequest(OssmDeviceEvent action) : IDisposable
{
    public OssmDeviceEvent Action { get; } = action;
    public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    public Subject<BleCharacteristicResult> Transfer { get; } = new();
    public TaskCompletionSource<JsonObject> Issued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public JsonObject? Request { get; set; }
    public Task<SetResult>? Operation { get; set; }
    public Task<SetResult>? Replacement { get; set; }
    public OssmDeviceEvent? ReplacementSettings { get; set; }
    public bool ReplacementCancelled { get; set; }
    public Task? Stopping { get; set; }
    public bool TransferSettled { get; private set; }
    public bool Terminal { get; set; }
    public bool Rejected { get; set; }
    public SetStatus? Interrupted { get; set; }

    public void SettleTransfer()
    {
        if (TransferSettled)
            return;
        Transfer.OnNext(BleTestContext.WriteResult([]));
        Transfer.OnCompleted();
        TransferSettled = true;
    }

    public void Dispose()
    {
        Transfer.Dispose();
        Cancellation.Dispose();
    }
}
