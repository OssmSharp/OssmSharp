# OSSM web controls

Control the OSSM-RS simulator from a browser. **Local only: no physical device
or remote access.** Use admin and guest tabs on the same computer.

## Quick start

Requires .NET 10, Rust/Cargo, and the `ossm-rs` checkout beside this repository
at `..\ossm-rs`. The sibling `..\retro-webui` checkout must also contain its
private widget package at `artifacts\packages\RetroWebUi.Blazor.0.2.0.nupkg`.
See [local widget setup](.docs/retro-widgets.md) if it is missing.

Run from this repository:

```powershell
dnx Aspire.Cli@13.5.3 -- run --project src\OSSMDotNet.AppHost\AppHost.csproj
```

Keep the terminal open. Aspire builds and starts the application.

## DeviceBridge library

DeviceBridge is also an independently buildable and consumable .NET feature.
`OSSMDotNet.DeviceBridge.Abstractions` contains the dependency-free
`IDeviceBridge`, `DesiredState` and `DeviceSample` contract. The separate
`OSSMDotNet.DeviceBridge` library contains the current simulator process
implementation; it does not depend on ControlService, Aspire or either web host.

The simulator implementation requires the separately built
[`ossm-sim-bridge`](tools/ossm-sim-bridge/README.md) executable at runtime.
ControlService owns one instance when running this application. This library
boundary is not a published package, a separate network service, a native-device
implementation or physical-device qualification. See the
[device-control specification](.docs/specs/device-control/spec.md) for the
contract and safety boundaries.

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
