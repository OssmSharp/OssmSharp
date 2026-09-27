#if MACOS
using Xunit.MicrosoftTestingPlatform;

namespace OssmSharp.Cli.IntegrationTests;

internal static class NativeTestProgram
{
    private static Task<int> Main(string[] args)
    {
        // A headless runner cannot pump Apple's main queue for MTP continuations.
        SynchronizationContext.SetSynchronizationContext(null);
        return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions);
    }
}
#endif
