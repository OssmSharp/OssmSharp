# CLI validation without Xcode

Investigated 27 September 2026. This report covers software checks only; no
physical device was accessed.

## Finding

The Stock-enabled [CLI](../../src/OssmSharp.Cli/OssmSharp.Cli.csproj) targets
`net10.0-macos` on this Mac. A full build enters the installed .NET macOS
workload's `_DetectSdkLocations` target, which rejects
`/Library/Developer/CommandLineTools`: it requires a valid Xcode app bundle.
The macOS 26.5 SDK, `clang`, and `swift` are available through Command Line
Tools, but their presence does not satisfy the workload check. This is a
build-toolchain requirement of the selected target, not a requirement imposed
by the simulator, ordinary C# compilation, or the CLI parser. Microsoft's
[Apple-workload guidance](https://aka.ms/macios-missing-xcode) also directs
developers to install/select Xcode for Apple-targeted builds.

No supported way to produce a complete **Stock-enabled macOS executable on
this machine without Xcode** was identified under the current project and
package contracts. Bypassing the workload check or manufacturing an app bundle
from compile-only outputs would not establish a valid build or test run.
Replacing the Apple target or BLE provider to avoid Xcode would be an
architectural change, not a test-run option.

## What works without Xcode

| Check | Result | What it proves |
| --- | --- | --- |
| Portable CLI `--help` process | Exit 0 | The `net10.0` simulator-only CLI builds and starts. |
| CLI unit tests | 93 passed, 0 failed, 0 skipped | Command grammar and foreground lifecycle with controlled dependencies. |
| Stock unit tests | 177 passed, 0 failed, 0 skipped | Stock behavior, codecs, and fake BLE boundaries. |
| Simulator process integration | 6 passed, 0 failed, 0 skipped | Real simulator child startup, readiness and cleanup through the in-process adapter boundary. |
| Native macOS 26.5 C# compile-only | Passed earlier with 15 warnings, 0 errors | The native CLI and Stock provider C# sources type-check against macOS and Shiny references. It does not create a runnable app. |
| Full Stock-enabled macOS build | Failed before native build, 0 tests run | The installed macOS workload requires an Xcode app bundle; Command Line Tools alone are rejected. |

These are the portable checks, after building the pinned simulator bridge as
described in the [README](../../README.md):

```sh
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj -- --help
dotnet test --project tests/OssmSharp.Cli.Tests/OssmSharp.Cli.Tests.csproj
dotnet test --project tests/OssmSharp.Device.Stock.Tests/OssmSharp.Device.Stock.Tests.csproj
dotnet test --project tests/OssmSharp.Cli.IntegrationTests/OssmSharp.Cli.IntegrationTests.csproj --filter-class '*SimulatorPromptIntegrationTests'
```

The successful native C# check used MSBuild
`-t:Restore,ResolveReferences,CoreCompile` with `EnableStockBle=true` and
`StockMacOSApiVersion=26.5`. A subsequent repeat could not reach compilation
because this execution environment denied NuGet's migration mutex; that retry
is **not** another passing compile. The earlier successful compile remains the
available evidence.

## Limits and separate prerequisite

Portable `net10.0` builds exclude `#if WINDOWS || MACOS` code in the
[native BLE connection](../../src/OssmSharp.Device.Stock/NativeStockBleConnection.cs)
and [discovery](../../src/OssmSharp.Device.Stock/StockBleDiscovery.cs).
The restored native Stock project selects
`Shiny.BluetoothLE`'s `net10.0-macos26.0` asset rather than its portable
`net10.0` asset. Therefore the passing portable Stock tests do **not** execute
the macOS Shiny registration, discovery, connection, or Apple Bluetooth
permission behavior. Native test sources compiled previously with a library
output override, but that did not run the native xUnit/MTP test application.
Windows-native behavior is also unverified on this Mac.

Real CLI simulator launch and attach process tests remain outstanding. They
exercise the simulator process boundary and do not require Xcode or physical
hardware.

## Practical next steps

1. Keep using the portable tests and native C# compile-only check for local,
   Xcode-free development. Report native BLE execution as unverified, not as
   passed. The no-Xcode commands and their scope are in the
   [README](../../README.md).
2. Rerun the real CLI simulator process tests to verify launch, attach and
   cleanup. This does not require Xcode or hardware access.
3. If a complete macOS Stock build is required, use an authorized Mac build
   environment with the compatible full Xcode installation. A compatible,
   trusted prebuilt app *may* run on a Mac without Xcode installed, but that
   has not been demonstrated for this CLI and still needs normal platform
   packaging and permissions. No CI workflow or prebuilt
   artifact was found in this checkout, and no push or external build was
   authorized.
4. Windows 11 can validate its own native Stock build without Xcode, but
   Windows results would not validate the macOS BLE implementation. Live
   physical-device checks require separate explicit authorization.

**Recommendation:** Do not redesign the Stock provider merely to remove a
build-machine tool requirement. Continue the Xcode-free checks above; treat
full macOS build and runtime qualification as an explicit outstanding gate.
