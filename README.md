# OSSM web controls

Control the OSSM-RS simulator from a browser. **Local only: no physical device
or remote access.** Use admin and guest tabs on the same computer.

## Quick start

Requires .NET 10 and Rust/Cargo. The sibling `../retro-webui` checkout must
contain its private widget package at
`artifacts/packages/RetroWebUi.Blazor.0.2.0.nupkg`. See
[local widget setup](.docs/retro-widgets.md) if it is missing.

Run from this repository:

```powershell
dnx Aspire.Cli@13.5.3 -- run --project src/OSSMDotNet.AppHost/AppHost.csproj
```

Keep the terminal open. Aspire builds and starts the application.

## Device libraries

The device API is split into three independently buildable libraries:

- `OSSMDotNet.Abstractions` contains `IOssmDevice` and immutable device models.
- `OSSMDotNet` contains `DeviceBase`, the stock protocol codecs and the
  stock-firmware `StockOssmDevice` transport placeholder.
- `OSSMDotNet.Simulator` contains the native-process `SimulatedDevice`.

These libraries do not depend on ControlService, Aspire, or either web host.

`OSSMDotNet.Simulator` requires the separately built
[`ossm-sim-bridge`](tools/ossm-sim-bridge/README.md) executable at runtime. The
library boundary is not a published package, a separate network service, or
physical-device qualification. See the
[device-control specification](.docs/specs/device-control/spec.md) for the
contract and safety boundaries.

### Standalone simulator diagnostics

Build the native bridge, then inspect or move the simulator without
ControlCore, Aspire or either web application:

```powershell
cargo build --locked --release --manifest-path tools/ossm-sim-bridge/Cargo.toml
$simulatorPath = (Resolve-Path tools/ossm-sim-bridge/target/release/ossm-sim-bridge.exe).Path
dotnet run --project src/OSSMDotNet.DeviceCli/DeviceCli.csproj -- start --simulator-path $simulatorPath
dotnet run --project src/OSSMDotNet.DeviceCli/DeviceCli.csproj -- status
dotnet run --project src/OSSMDotNet.DeviceCli/DeviceCli.csproj -- move --speed 20 --stroke 20 --depth 50 --seconds 3
dotnet run --project src/OSSMDotNet.DeviceCli/DeviceCli.csproj -- stop
```

`start` launches one headless simulator host for the current user and is safe to
repeat with the same bridge path. `status` and `move` reuse that host. `move`
supports Simple Stroke with sensation `50` and buffer `0`, settles at zero speed
after the bounded duration, and leaves the host running. `stop` settles zero
speed and ends the host. The bridge has no UI; the separate OSSM-RS `ossm-sim`
project contains a browser UI, but it is not this library integration. This is a
software diagnostic path, not physical-device qualification.

### Real-device CLI (read-only preview)

The opt-in macOS CLI can scan for an **explicitly selected platform peripheral
ID**, connect without auto-reconnect and read the stock pairing characteristic.
It prints the reported Wi-Fi MAC, chip and firmware details. It sends no GATT
writes, does not start the simulator and cannot issue physical motion. The
stock-device motion adapter remains unimplemented; physical operation requires
[device qualification](.docs/specs/device-control/spec.md#physical-qualification).

With .NET 10, the `macos` workload and a full Xcode installation selected
(Command Line Tools alone are insufficient), the intended command is:

```sh
dotnet run --project src/OSSMDotNet.DeviceCli/DeviceCli.csproj --property:EnableStockBle=true -- identify --peripheral-id YOUR_SELECTED_MACOS_PERIPHERAL_ID
```

Obtain the macOS peripheral ID independently; this preview does not list
candidates or enroll an identity. Never substitute a name, signal strength or
discovery order for an explicitly selected ID. Confirm the live Wi-Fi MAC and
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
control** again before the guest can send more. The operator UI/API is
unauthenticated and bound only to `127.0.0.1`. There is no operator access
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
- Invitations are kept only in admin memory. Restarting the app changes the
  link.
- If control is **Locked**, wait for **Unlock** to become enabled, click it in
  admin, then arm separately when ready.
- Press **Ctrl+C** in the terminal to shut down the application and simulator.
