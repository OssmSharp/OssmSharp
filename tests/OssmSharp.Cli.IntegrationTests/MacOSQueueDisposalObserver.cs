#if MACOS
using Shiny.BluetoothLE;

namespace OssmSharp.Cli.IntegrationTests;

internal sealed class MacOSQueueDisposalObserver(AppleBleConfiguration configuration) : IDisposable
{
    internal int DisposeCount { get; private set; }

    internal bool QueueWasAliveOnDispose { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        QueueWasAliveOnDispose = (IntPtr)configuration.DispatchQueue!.Handle != IntPtr.Zero;
    }
}
#endif
