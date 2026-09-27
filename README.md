# OSSM web controls

Control the OSSM-RS simulator from a browser. **Local only: no physical device
or remote access.** Use admin and guest tabs on the same computer.

This M3-to-M4 transition is not a deployable release. Web.Api connects to a
separately started TCP simulator; the CLI can start its own simulator process
or attach to an existing loopback listener. Neither path is physically qualified.

## Quick start

Requires .NET 10 and Rust/Cargo. The sibling `../retro-webui` checkout must
contain its private widget package at
`artifacts/packages/RetroWebUi.Blazor.0.2.0.nupkg`. See
[local widget setup](.docs/retro-widgets.md) if it is missing.

Run from this repository:

```powershell
dnx Aspire.Cli@13.5.3 -- run --project src/OssmSharp.AppHost/OssmSharp.AppHost.csproj
```

Keep the terminal open. Aspire builds and starts the application.

The admin, guest and API hosts also write structured JSON logs to
`%LOCALAPPDATA%\OSSMDotNet\logs\<application>-YYYYMMDD.json` on Windows.
Files roll daily and at 10 MiB; at most seven files per application are kept,
and files older than seven days are removed. Frequent size rolls may therefore
shorten the effective history. Set `Logging__File__Directory` to an absolute
path to change the directory, or `Logging__File__Enabled=false` to disable file
logging. Console output and Aspire telemetry remain available.

## Device libraries

The device API currently has four independently buildable libraries:

- `OssmSharp.Device.Primitives` contains `IOssmDevice` and immutable device models.
- `OssmSharp.Device.Runtime` contains the shared `DeviceBase`, options and
  device lifecycle logging; shared ownership and interruption mechanics remain planned.
- `OssmSharp.Device.Stock` contains stock protocol codecs and a read-only BLE
  connection; physical motion writes remain unavailable.
- `OssmSharp.Device.Simulator` contains the loopback TCP `SimulatedDevice` adapter.

These libraries do not depend on Web.Application, Aspire, or either web host.
`OssmSharp.Cli` is the only CLI; it uses the device libraries directly,
not Web.Api.

`OssmSharp.Device.Simulator` connects to an
[`ossm-sim-bridge`](tools/ossm-sim-bridge/README.md) on IPv4 loopback. The
CLI prompt starts and owns a bridge only when given an executable path; Web.Api
and the CLI's endpoint-attachment mode use an independently started listener. The
library boundary is not a published package, a remotely exposed service, or
physical-device qualification. See the
[device-control specification](.docs/specs/device-control/spec.md) for the
contract and safety boundaries.

### Independent simulator listener

Build and start the native bridge without AppHost or either web application:

```powershell
cargo build --locked --release --manifest-path tools/ossm-sim-bridge/Cargo.toml
$bridge = if ($IsWindows) { 'ossm-sim-bridge.exe' } else { 'ossm-sim-bridge' }
& (Resolve-Path "tools/ossm-sim-bridge/target/release/$bridge").Path --listen 127.0.0.1:18083
```

Configure a separately started Web.Api with
`OSSM__Control__SimulatorEndpoint=tcp://127.0.0.1:18083` along with its service
keys, guest origin, and listener settings. Disconnecting the adapter leaves
the simulator running at zero speed; stop the native process in its terminal.
AppHost can start the listener for local web development, but the API does not
require AppHost. The old CLI daemon and named-pipe commands have been removed.

### Simulator CLI prompt

Build the bridge, then start a CLI-owned simulator session from the repository
root. The machine-wide `OssmSharp/runtime` ownership directory must already be
provisioned for the operator under Windows CommonApplicationData or macOS
`/Library/Application Support`.

```powershell
cargo build --locked --release --manifest-path tools/ossm-sim-bridge/Cargo.toml
$bridge = if ($IsWindows) { 'ossm-sim-bridge.exe' } else { 'ossm-sim-bridge' }
$path = (Resolve-Path "tools/ossm-sim-bridge/target/release/$bridge").Path
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj -- prompt --simulator $path
```

Inside the prompt, use `status`, `set --speed 12 --stroke 20 --depth 50
--pattern SimpleStroke`, `stop`, and `quit`. The CLI attempts a serialized zero
before closing the device, then stops only the simulator process it started.
Write settlement is not independent proof of motion or physical stopping.
The native bridge and simulator adapter passed local integration checks, but
real CLI launch and attach could not be verified on this Mac because its
machine-wide ownership directory is not provisioned. No physical device was
used.

Alternatively, leave an independently started bridge running and attach without
giving the CLI ownership of that process:

```powershell
& $path --listen 127.0.0.1:18083
# In another terminal:
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj -- prompt --simulator tcp://127.0.0.1:18083
```

The CLI disconnects and attempts Stop on exit but does not terminate an
externally started bridge. Only `tcp://127.0.0.1:<port>` (ports 1-65535) is
supported; a remote address is rejected rather than silently interpreted as
an executable path.

### Stock discovery and read-only CLI

Opt-in native CLI builds for Windows 11 and macOS 26+ expose `devices list`,
`devices get <id>` and `prompt --bluetooth <id-or-name>`. List shows discovered
platform IDs and names without connecting. Get requires a platform ID and reads
that device's live pairing identity without a numbered selector or motion writes.
The Bluetooth prompt resolves the ID first, otherwise requires one exact and
unambiguous discovered name. Connection cannot arm motion; Stock motion remains
unsupported pending separate Runtime/Stock work and physical
[qualification](.docs/specs/device-control/spec.md#physical-qualification).

With .NET 10, the platform workload and native tooling installed, run:

```sh
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj --property:EnableStockBle=true -- devices list
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj --property:EnableStockBle=true -- devices get YOUR_PLATFORM_ID
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj --property:EnableStockBle=true -- prompt --bluetooth YOUR_PLATFORM_ID
```

The reported Wi-Fi MAC is device information, not a separately enrolled
connection prerequisite. A missing or ambiguous name fails; retry with an
explicit platform ID. Get prints the reported Wi-Fi MAC, chip, Wi-Fi connection
status, sketch MD5 and firmware version alongside the selected ID and name.
Neither discovery nor connection implies that physical operation is qualified.

The macOS Stock build targets `net10.0-macos` and imports the .NET macOS
workload. Its full build checks for an Xcode app bundle; Apple's Command Line
Tools alone do not satisfy that check. Xcode is not needed for the portable
`net10.0` CLI and simulator checks:

```sh
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj -- --help
dotnet test --project tests/OssmSharp.Cli.Tests/OssmSharp.Cli.Tests.csproj
dotnet test --project tests/OssmSharp.Device.Stock.Tests/OssmSharp.Device.Stock.Tests.csproj
dotnet test --project tests/OssmSharp.Cli.IntegrationTests/OssmSharp.Cli.IntegrationTests.csproj --filter-class '*SimulatorPromptIntegrationTests'
```

These checks passed locally: 93 CLI unit tests, 177 deterministic Stock tests,
and six simulator process integration tests. The macOS 26.5 native CLI and
Stock C# sources also passed a compile-only check. This does not build or run
the macOS BLE executable; a local full build requires Xcode, and native BLE
runtime validation remains outstanding. Windows-native behavior needs a Windows
environment. Full CLI simulator process tests additionally need the provisioned
machine-wide ownership directory described above; they must not substitute a
per-user lock. No hardware has been accessed in this work. See the
[no-Xcode investigation](.docs/plans/cli-no-xcode-investigation.md) for
the evidence and alternatives, and the
[device adapter design](.docs/specs/device-control/adapters.md#physical-device-selection-and-connection)
for remaining integration and qualification requirements.

## Use it

1. Open the **Dashboard** URL printed in the terminal. Use the complete link,
   including its login token.
2. In the dashboard's **Resources** page, wait for **admin** and **guest** to
   become healthy. On the **admin** resource, click **Operator (local only)**.
   This opens the control page without an operator login or token.
3. The guest invitation is ready automatically. Click **Copy** and open the link
   in a second browser tab. Keep the admin tab open.
4. The guest joins automatically. Return to admin and click **Allow control**.
5. In the guest tab, set the three vertical sliders and click **Send**. Enable
   **Auto send** to send slider changes automatically.

Session limits default to **100%** and do not need a setup step. Lower them and
click **Apply limits** if needed. Applying compatible limits keeps control
running without pausing or re-arming. If the new bounds conflict with current or
pending motion, the entire change is rejected. Explicitly **Stop** and wait for
stopped feedback before retrying those bounds. Applying limits never extends the
guest's motion lease.

Purple is selected, amber is sent, and green is the verified current setting.
The live display shows actual simulator position and velocity. On the operator
page, expand **Live feedback** to see it. **Live log** lists recent activity
with the newest entries at the bottom and follows new entries; scroll up to read
earlier entries.

Settings keep running until **Stop**. Switching tabs, losing focus or losing the
connection also stops control and turns Auto send off. Admin must **Allow
control** again before the guest can send more. The operator AdminWeb UI is
unauthenticated and bound only to `127.0.0.1`; Web.Api authenticates its host
service key and enforces operator and guest roles. There is no operator access
timeout. Host/Origin checks remain enforced; do not expose or proxy the admin
listener remotely. The invitation has no expiry and is reusable for this
application run, one guest at a time. Guest approval and session leases still
expire; every new guest session needs **Allow control**. Opening or rejoining
either page never arms motion. Do not share the dashboard login link or guest
invitation with unintended users.

## Stop or reset

- **Stop** requests a stop without ending the session.
- **Stop and end session** disconnects the guest, requests serialized Stop and
  automatically rotates the invitation. The old link no longer works.
- Guest leave, disconnect and session timeout keep the invitation for a new
  pending session, with fresh approval and explicit arming.
- **Cycle link** replaces the invitation immediately, revokes the previous guest
  and requests a serialized Stop. Old links, guest proofs and control grants no
  longer work. The replacement never enables control automatically or clears a
  lock.
- Invitations are kept only in Web.Api application memory. Restarting the API changes the
  link.
- If control is **Locked**, wait for **Unlock** to become enabled, click it in
  admin, then arm separately when ready.
- Press **Ctrl+C** in the terminal to shut down the application and simulator.
