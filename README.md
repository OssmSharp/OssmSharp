# OSSM web controls

Control the OSSM-RS simulator from a browser. **Local only: no physical device
or remote access.** Use admin and guest tabs on the same computer.

This M3-to-M4 transition is not a deployable release. Web.Api connects to a
separately started TCP simulator; the CLI starts its own simulator process only
for an explicit `prompt --simulator` session. Neither path is physically qualified.

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
CLI prompt starts and owns one bridge process; Web.Api uses an independent one. The
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
The native and CLI projects have built, but the new session has not been run
or qualified on a live device in this checkpoint.

### Real-device CLI (read-only preview)

The opt-in macOS CLI can scan for stock devices and ask for a numbered selection,
or scan for an **explicitly supplied platform peripheral ID**. It connects
without auto-reconnect and reads the stock pairing characteristic.
It prints the reported Wi-Fi MAC, chip and firmware details. It sends no GATT
writes, does not start the simulator and cannot issue physical motion. The
stock-device motion adapter remains unimplemented; physical operation requires
[device qualification](.docs/specs/device-control/spec.md#physical-qualification).

With .NET 10, the `macos` workload and a full Xcode installation selected
(Command Line Tools alone are insufficient), the intended command is:

```sh
dotnet run --project src/OssmSharp.Cli/OssmSharp.Cli.csproj --property:EnableStockBle=true -- device get
```

The command lists `Device name (platform ID)` with numbers, then asks for a
number and Enter. It requires an interactive terminal; supply
`--peripheral-id YOUR_SELECTED_MACOS_PERIPHERAL_ID` for noninteractive use.
The preview does not enroll an identity. Never select a device automatically by
name, signal strength or discovery order. Confirm the live Wi-Fi MAC and
firmware version before saving the locator and expected MAC in local
configuration. On every later connection, verify both again. Discovery and
connection must not arm or restore motion; only qualified physical control may
issue bounded commands and verify Stop.

The macOS BLE source has passed a compile-only check, but this machine lacks
full Xcode, so the native app build and device behavior have **not** been
validated. No hardware was accessed. See the
[device adapter design](.docs/specs/device-control/adapters.md#physical-device-selection-and-connection)
for the remaining enrollment and integration requirements.

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
