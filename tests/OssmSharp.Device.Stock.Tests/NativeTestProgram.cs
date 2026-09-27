#if MACOS
using Xunit.MicrosoftTestingPlatform;

namespace OssmSharp.Device.Stock.Tests;

internal static class NativeTestProgram
{
    private static Task<int> Main(string[] args)
    {
        // This headless runner does not pump Apple's main queue; MTP continuations must use the thread pool.
        SynchronizationContext.SetSynchronizationContext(null);
        return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions);
    }
}
#endif
