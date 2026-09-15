# Capability map: OSSM remote control

Status: The user's clarification on 13 September 2026 establishes one guest
browser and two separately running .NET 10 services: a restricted guest web
service and a private admin service. Local one-box operation, one-click session
links and accountless access remain required. The admin service owns session
authority, policy and device execution through a bridge. This supersedes the
earlier single-process design. Existing module IDs and replaceability
requirements are retained; module specifications and implementation still have
their own approval gates.

On 14 September 2026 the user approved the simulator-first prototype plan and
dependency additions. The implementation scope is the local, two-process
operator/guest flow, using the existing OSSM-RS simulator engine. Native
hardware control and physical qualification remain excluded.

See [prototype contract and delivery scope](prototype.md) and
[repository instructions](../../AGENTS.md) for the current implementation
requirements, including xUnit v3, MTP v2, FastMoq and memory ownership.

Source: User-provided "OSSM self hosted remote control handoff", dated 13
September 2026, as amended by the conversation. The user's service-owned
architecture takes precedence over the handoff's browser-owned device path. This
describes proposed behavior, not an implemented or safety-certified system.

Architecture views:
[deployment and connections](../diagrams/architecture.html#deployment) and
[control enforcement](../diagrams/architecture.html#enforcement).

## Objective

Add temporary, explicitly approved remote control to one stock-firmware OSSM:
separate guest/admin processes and one active control session. The guest browser
may be remote or on the same computer. It talks only to the guest web origin,
never directly to admin, bridge or device. Administrative controls use a private
local admin surface, not guest routes. Only the admin service authorizes device
execution and owns the .NET bridge.

## Repository baseline and assumptions

At inspection, this repository was on `main` at `3637ee6` and tracked only
`.gitignore`. No application, BLE library, firmware, build commands, tests, or
documentation convention were available locally.

The guest service is an ASP.NET Core 10 Blazor Web App using Interactive Server
rendering. A separate .NET 10 admin host provides local management, session
authority, policy and the device-control hosted service. Chrome is the first UI
target, not a Bluetooth runtime. The revised
[device-control specification](device-control/spec.md) records this change, the
comparison with other projects and its trade-offs. The local prototype now uses
Aspire AppHost/ServiceDefaults, Blazor and minimal APIs. Hardware behavior and
physical safety remain unqualified.

Reference application, not an import baseline:
[`e65032ba2ed9c5709d91e5afd4f7034d7965fa32`](https://github.com/ReadieFur/OSSM-Web-Control/tree/e65032ba2ed9c5709d91e5afd4f7034d7965fa32),
committed on 19 May 2026. GitHub identifies the application licence as GPL-3.0;
its bundled BLE library has an
[Apache-2.0 licence](https://github.com/ReadieFur/OSSM-Web-Control/blob/e65032ba2ed9c5709d91e5afd4f7034d7965fa32/src/ossm-ble/LICENCE).
The browser BLE source reference is
[`d2a9de6cb8fbdeaba5a2d4345c69d7a70d4789f2`](https://github.com/ReadieFur/OSSM-BLE-Web/tree/d2a9de6cb8fbdeaba5a2d4345c69d7a70d4789f2).
Neither this TypeScript library nor its generated bundle is selected for runtime
reuse or mechanical translation. Implement stock protocol mapping and bridge
access in C#, using the pinned firmware sources as evidence. The new application
remains planned as Apache-2.0; no third-party application code or product assets
are selected for import. Prototype dependencies were subsequently approved;
unrelated additions remain gated.

The six earlier controller findings in handoff section 2 have corresponding
patterns in the inspected application/library sources. They are not yet
reproduced regressions in this checkout. They inform behavior tests for the C#
controller; discarded application/library code is not an inherited defect
backlog. Any defect actually reproduced in retained code still requires
test-first correction.

## Modules and service ownership

These are logical specification boundaries. The runtime split below is mandatory
even when both processes share one machine. Dependencies describe provider
contracts and integration prerequisites.

| Module id        | Responsibility                                                                                                                                      | Depends on                                          | Handoff sections      |
| ---------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------- | --------------------- |
| `device-control` | Admin-owned C# controller and .NET bridge; initialization, single-writer execution, Stop, external-control lockout, disposal and qualified feedback | None                                                | 2, 5, 9, 13, 14       |
| `session-relay`  | Guest ingress and versioned private protocol; admin-owned invitation/admission/session authority; guest-side parsing and quotas                     | None                                                | 3, 4, 6, 7, 8, 9, 11  |
| `host-authority` | Admin-service policy; operator approval/arming/unlock, limits, grants, freshness and revocation through the controller                              | `device-control`, `session-relay`                   | 3, 4, 5, 6, 9, 11, 13 |
| `guest-controls` | Three vertical sliders, automatic invitation redemption, manual/automatic Send, Stop and verified live state                                        | `session-relay`, `host-authority`                   | 4, 6, 7, 9, 13        |
| `self-hosting`   | Two-process one-box deployment, guest HTTPS ingress, private admin bootstrap/IPC, bridge access and least privilege                                 | `session-relay`, `host-authority`, `guest-controls` | 7, 8, 10, 11, 13      |

Retained dependency order: `device-control` -> `session-relay` ->
`host-authority` -> `guest-controls` -> `self-hosting`.

The historical IDs remain stable. `session-relay` owns the boundary between
guest ingress and admin-owned session authority, not a relay to another browser.
`host-authority` and `device-control` run only in admin. There is one device
controller, never a copy in the guest process or a UI circuit.

Guest ingress rejects malformed/over-quota input early. Admin independently
validates the guest capability, current session, grant and policy before its
controller serializes execution. Guest-process validation is not a substitute
for admin checks. Only admin may invoke the private bridge.

The session boundary owns the requirement for authenticated session creation.
Self-hosting supplies the unauthenticated, loopback-only operator surface. Do
not defer that requirement by exposing an unauthenticated create route.

## Separate guest and admin services

Run two independently started .NET processes with separate listeners and
privileges, not merely two route groups in one app. Guest UI rendering and
public traffic terminate in the limited guest process. Management, authoritative
session state and device execution live in the private admin process. Shared
libraries contain contracts, not shared mutable authority.

| Runtime                        | Technology and ownership                                                                                                                                                            |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Guest browser, local or remote | Chrome-first UI talking only to the guest web service; no admin/device access                                                                                                       |
| Guest web service              | ASP.NET Core 10 server-side Blazor; Join, version-2 run input, Stop request, leave and scoped live status only                                                                      |
| Admin service                  | Separate .NET 10 process; private operator UI/API, link generation, invitation redemption/admission, authoritative sessions/policy, grants and single device-control hosted service |
| Device bridge                  | .NET adapter inside admin; native I/O only for admin's controller; not accessible to guest web                                                                                      |
| OSSM                           | Stock firmware and hardware reached only by the configured bridge                                                                                                                   |

Guest pages and WebSocket/SignalR use one configured guest origin: HTTPS
remotely, with the loopback-only exception below. Admin has a distinct,
local-only management listener. Never proxy admin routes through the guest
origin or enable guest-origin CORS against admin. The same browser can be used
locally without an operator token; this does not give guest pages operator
authority or introduce a browser-owned device controller.

The guest/control message schema remains versioned independently of the UI
transport. Blazor circuit events, minimal API endpoints and socket handlers must
all enter the same restricted guest application boundary. Framework rendering,
reconnect or SignalR delivery is not motion authorization. Circuit reconnection
must not replay inputs or restore an expired grant.

Use a versioned private IPC contract between guest and admin on one machine,
with OS-enforced peer access and a credential restricted to guest forwarding.
Use a named pipe or Unix-domain socket appropriate to the selected OS. Guest web
must not read device credentials or native device handles. OS service identities
for isolation do not introduce application user accounts.

The guest-forwarding interface permits only invitation redemption, scoped
input/Stop/leave and session-scoped status/grants. Each request also carries the
relevant guest proof, which admin validates itself; service authentication alone
does not authorize control. It has no create-link, approve, arm, change-policy,
unlock-device, enumerate-device or arbitrary-command method. Admin/operator API
and guest-forwarding API use separate allowlists; only guest forwarding requires
a service credential. Local admin is unauthenticated and must not be reachable
from a remote guest origin.

Do not mint fresh guest intent from a server-side held value. Admin issues grant
deadlines on its own monotonic clock and rejects delayed/replayed forwarded
requests. Closing the IPC connection or losing guest web invokes admin's
Stop/teardown path; if closure is not observed, guest-grant expiry still
applies. Admin restart invalidates all sessions and capabilities; guest web
cannot restore them from cached UI state.

A compromised guest service may forge input within a guest capability it
possesses. Admin bounds forwarded input by session policy and fresh grants. The
unauthenticated loopback admin listener is also reachable by local processes;
this split does not isolate compromised code on the same computer or prove human
intent.

The actual admin-host OS, Bluetooth access and process permissions must be
established before choosing the native bridge. Simulator mode is the default.

If deployment later places the bridge or guest/admin services on different
machines, that private service boundary needs authenticated, versioned commands,
freshness and disconnect behavior before use; it is not a guest endpoint.
Rootless Podman is optional only where the selected bridge's OS/device
permissions work without privileged mode or a container-engine socket.

Guest release, hidden UI, disconnect or stale input invokes Stop in the
still-running admin service. Admin's clock expires guest grants even if the
browser or guest web has crashed. Admin/OS/bridge failure still needs
independent device-side protection; a hosted-service timer is not hardware
safety. Never restore motion on reload, service restart or reconnection.

Detected external parameter changes, including physical remote/knob input, must
invoke the
[latched lockout in DC-09](device-control/spec.md#dc-09-external-parameter-changes-latch-an-admin-only-lockout).
Admin cancels pending motion and requests Stop without restoring previous
settings. Only explicit admin Unlock can clear the latch, and successful unlock
leaves the device paused for separate rearming. Guest web has no unlock
operation. Physical detection and stop behavior remain qualification
requirements, not assumptions about the stock firmware.

## Functionality and package inventory

These are the prototype's providers. Project files contain the selected package
versions; the native hardware adapter remains unimplemented.

The proposed servicing target was .NET SDK **10.0.401** and runtime **10.0.12**,
from the
[official .NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
The local prototype uses the installed **10.0.303** SDK and **10.0.11** runtime
rather than installing another SDK implicitly. `global.json` records that build
baseline. Keep servicing updates deliberate. Framework APIs below ship with .NET
or the ASP.NET Core shared framework; do not add redundant PackageReferences
just because a namespace has the same name.

| Functionality                                                 | Provider and version                                                                                                                        | Where / limits                                                                                                                                |
| ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| Orchestration, builds, health and local dashboard             | `Aspire.AppHost.Sdk` `13.5.3` and its CLI bundle                                                                                            | Separate guest/admin projects, Cargo build resource, local-only endpoints; no custom launcher                                                 |
| Web hosting, minimal APIs, server-rendered Blazor and SignalR | `Microsoft.AspNetCore.App` shared framework, local runtime `10.0.11`                                                                        | Guest web and private admin host; no MVC controllers or WebAssembly controller                                                                |
| Background lifecycle, DI, configuration and options           | `Microsoft.Extensions.Hosting`, `DependencyInjection`, `Configuration`, `Options` from the shared framework                                 | Admin's one hosted device controller; independent guest web host                                                                              |
| Admin CLI parsing, help and commands                          | [`System.CommandLine` `2.0.12`](https://www.nuget.org/packages/System.CommandLine/2.0.12)                                                   | Local admin executable/CLI client; calls the same private management operations, never a parallel device writer                               |
| JSON serialization                                            | `System.Text.Json`, .NET 10                                                                                                                 | Explicit DTOs, closed parsing and bounded inputs; no CLR type-name dispatch                                                                   |
| Closed, versioned JSON contracts                              | `System.Text.Json`, .NET 10                                                                                                                 | Native duplicate/unknown-field rejection, required constructor parameters, nullable annotations and bounded messages; no extra schema package |
| Startup configuration validation                              | `Microsoft.Extensions.Options`, `System.ComponentModel.DataAnnotations`, shared framework                                                   | Typed options, `IValidatableObject`, `ValidateDataAnnotations` and `ValidateOnStart`                                                          |
| Token generation, hashes and constant-time comparison         | `System.Security.Cryptography`, .NET 10                                                                                                     | Admin mints capabilities; no account database, identity provider or JWT package needed                                                        |
| Endpoint authorization, antiforgery, rate limiting            | ASP.NET Core authorization/antiforgery/rate-limiting middleware, shared framework                                                           | Service-specific scopes and policies are application code; middleware alone does not enforce device limits                                    |
| Local guest/admin IPC                                         | `System.IO.Pipes` or `System.Net.Sockets`, .NET 10                                                                                          | OS-protected named pipe or Unix-domain socket, length-bounded framing, explicit peer/operation authorization; no message broker required      |
| Monotonic timing and bounded scheduling                       | `System.TimeProvider`, task primitives and `System.Threading.Channels`, .NET 10                                                             | Admin-owned deadlines/serialization; channels do not replace the single desired-state slot or priority Stop contract                          |
| Operational diagnostics                                       | `Microsoft.Extensions.Logging` / `ILogger<T>`; `Microsoft.Extensions.Logging.Abstractions` `10.0.0` in the bridge                           | Structured diagnostics; no capability URLs or payload histories                                                                               |
| Aspire telemetry                                              | `OpenTelemetry.Exporter.OpenTelemetryProtocol` `1.15.3`; Hosting/AspNetCore/Http/Runtime instrumentation `1.14.0`                           | Local ServiceDefaults telemetry; exporter updated from the template to address restore advisories                                             |
| Service discovery defaults                                    | `Microsoft.Extensions.ServiceDiscovery` `10.1.0`                                                                                            | Native Aspire defaults; guest control still uses restricted IPC, without automatic command retries                                            |
| Unit/contract tests                                           | [`xunit.v3.mtp-v2` `3.2.2`](https://www.nuget.org/packages/xunit.v3.mtp-v2/3.2.2)                                                           | User-selected xUnit v3 framework, explicitly integrated with MTP v2                                                                           |
| Test runner                                                   | [`Microsoft.Testing.Platform` `2.4.0`](https://www.nuget.org/packages/Microsoft.Testing.Platform/2.4.0)                                     | `global.json` selects MTP; no MSTest or VSTest runner                                                                                         |
| Test construction and collaborators                           | [`FastMoq.Core` `4.5.0`](https://www.nuget.org/packages/FastMoq.Core/4.5.0)                                                                 | FastMoq's core provider-neutral functionality where useful; retain deterministic I/O fakes for timing and fault behavior                      |
| Deterministic clock tests                                     | [`Microsoft.Extensions.TimeProvider.Testing` `10.0.0`](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing/10.0.0)     | Test-only `FakeTimeProvider`; no wall-clock sleeps for expiry proofs                                                                          |
| HTTP/process integration                                      | `Aspire.Hosting.Testing` `13.5.3`, native Kestrel fixtures                                                                                  | Aspire owns test application startup and cleanup; real simulator/IPC checks                                                                   |
| Logging test fixtures                                         | `Microsoft.Extensions.Diagnostics.Testing` `10.0.0`                                                                                         | Native `FakeLogger<T>` verifies lifecycle logging without credentials                                                                         |
| Simulator motion engine                                       | [`ossm-rs/ossm-rs` `ossm-motion`](https://github.com/ossm-rs/ossm-rs/tree/578161b2e78d8ca70f8909f0ec34b8ae6014cfda/ossm-motion), Apache-2.0 | Reuse the local sibling checkout through `tools\ossm-sim-bridge`; native headless test process, no hardware I/O                               |
| Native OSSM bridge                                            | No third-party package selected yet                                                                                                         | Resolve against actual host OS, BLE hardware, permissions and service-mode support; simulator until verified                                  |

Native JSON validation checks shape, not authorization or safe geometry.
Explicit policy code remains in admin. This replaces the earlier proposed
`JsonSchema.Net` dependency; no remote schema fetching is used.

`System.CommandLine` supplies parsing, help and async command dispatch, not
privilege. The local admin CLI uses admin's existing token-free command path and
supplies the matching Origin; no credential input is required. Guest credentials
must not be command-line arguments or diagnostic output. Root/subcommand help,
invalid-input exit codes, redirected output and cancellation get dedicated
tests. CLI setup must not turn one-click browser link generation into a
multi-command per-session flow.

No EF Core/database, ASP.NET Core Identity, browser Bluetooth package,
JavaScript frontend toolchain, generic workflow engine, broker or terminal
decoration library is selected. Add a dependency only for a concrete capability
after approval; do not install optional packages pre-emptively.

## Required deployment and invitation experience

### DEP-01: Local one-box operation

The browser, both .NET service processes, controller and admin's .NET bridge
must be able to run on one supported computer connected to OSSM. One-box means
one computer, not collapsing the two services into one process. Local operation
must not require a second server, reverse proxy, tunnel, cloud identity
provider, public DNS or an internet connection after installation. Serve runtime
assets locally; use the accountless local bootstrap in SESSION-02.

Admin always binds exclusively to `127.0.0.1`. HTTP/WS is permitted only on that
loopback boundary; remote guest operation requires explicitly configured
HTTPS/WSS exposure. Never expose or proxy admin remotely. Do not bind all
interfaces, change firewall rules or start a tunnel merely to make a link work.
Containerization is optional, not a prerequisite for one-box operation.

The local operator UI/API is intentionally unauthenticated. No operator token,
login or access timeout is required. Retain exact Host and Origin checks and
applicable CSRF protection, including for state changes and WebSockets. Any
local process able to reach admin can perform management actions; this is not
isolation from compromised code on the same computer. The local browser can use
the separate admin management origin directly; no additional browser or
browser-based BLE client is required.

Local and remote guests use the same guest authorization, session, policy and
single-writer path. Being on one computer never disables guest permissions,
limits, grant expiry, Stop ordering or physical qualification.

Acceptance: with external networking unavailable, one computer can start both
services, open the local operator surface, create a local session link and
exercise the control flow against a simulator without operator credentials.
Wrong Host or missing/foreign mutation Origin requests are denied. Hardware
operation additionally needs the qualified native bridge.

### SESSION-01: One-click session-link generation

The 15 September 2026 user request supersedes the original single-use,
five-minute invitation. In the local unauthenticated admin surface, with the
advertised guest address configured, a reusable invitation is automatically
available as a complete ready-to-share URL. No creation click, separate room
action, manually entered session ID or token assembly is required. Provide a
Copy action; report clipboard failure without hiding the selectable URL.

Admin owns creation, generates the session ID and high-entropy invitation
secret, and returns the link only after successful creation. The invitation has
no expiry and uses the form `<guest-origin>/join/<sid>#invite=<secret>`,
preserving the validated full origin's scheme and port. HTTP is valid only for
loopback; all other origins require HTTPS. Credentials must not enter query
strings, request logs or retained browser storage. Invitation redemption, guest
admission and arming remain separate: retrieving or opening a link does not
authorize motion. Keep the invitation separate from the active guest session.
Only one guest may be pending or approved; after leave, transport loss or
session expiry, the same link permits a new pending session with a fresh proof,
new approval and explicit arming. Invitation reuse does not extend the pending
or approved guest leases. The operator's **Stop and end session** action instead
atomically rotates the invitation while revoking the guest and requesting
serialized Stop.

Build links from the configured advertised guest origin, never the admin origin
or an untrusted request Host header. A remote link requires an already
configured reachable HTTPS address. A loopback link is labelled **Local only -
this computer**; do not present it as remotely shareable. If remote exposure is
absent, explain the missing setup rather than generating a misleading localhost
invitation. This prerequisite does not require a wizard on each use.

Keep the current invitation memory-only for the admin application lifetime;
restart changes it. Ordinary reads and reloads return the same link without
device writes or changes to an active controller. Compatibility creation retries
remain scoped and idempotent, but cannot resurrect a link invalidated by Cycle
or operator Stop and end session.

Provide a distinct **Cycle link** action. Cycling atomically invalidates the
previous link and guest proof/connection/grants, clears pending input and
requests serialized Stop. Show the replacement immediately; settling I/O still
prevents arming. A new session does not clear external lock state or
automatically arm. Cycle leaves normal control paused and preserves existing
locked/faulted states. **Stop and end session** uses this same atomic operation,
not separate end/read steps; the old invitation and compatibility creation
attempts cannot revive.

Acceptance: a correctly addressed, nonexpiring link is available automatically;
guest forwarding cannot create or cycle links; repeated/concurrent reads do not
create multiple invitations or admit multiple guests; failures show no
success-shaped link; retrieval issues no device writes. Cycling while control is
active revokes old authority and follows the single-writer Stop path.
Local-only, configured remote and missing-remote-address cases have distinct,
accurate outcomes.

### SESSION-02: Accountless access

Accounts are out of scope: no registration, sign-in screen, user/profile
database, password management, ASP.NET Core Identity or external identity
provider is required. "Owner/operator" describes the local management role;
"guest" receives temporary scoped capabilities. Neither is a stored user account
or verified personal identity.

Admin's unauthenticated `127.0.0.1` surface permits session-link creation,
approval, policy configuration, arming, explicit device unlock and revocation.
There is no operator credential or access expiry. Host/Origin protection and the
local-only listener are required; no remote admin exposure is supported. Opening
the surface never arms or resumes motion.

The guest receives a reusable invitation from SESSION-01. Opening a valid
invitation forwards Join to admin for atomic reservation/redemption and
approval. Admin issues the session-scoped guest capability bound to the admitted
connection. Guest web cannot mint it. A guest capability never creates sessions,
changes policy, arms/unlocks the device or becomes operator authority. No
account creation or login is inserted before link generation or guest joining.

Guest session capabilities retain independently generated 256-bit CSPRNG
secrets, fixed expiries and server-enforced scopes. The nonexpiring invitation
grants admission only, never motion authority. Invitation links carry their
secret in the fragment and remove it promptly; subsequent credentials remain in
memory. Clear temporary guest authority on admin restart. Revoking or ending a
control session invalidates its guest capability and follows the existing Stop
path.

Do not retain secrets in telemetry, browser storage, source code or public
assets. Keep only hashes for token validation, except the bounded in-memory
invitation result needed for SESSION-01's app-lifetime retrieval. Tokens prove
possession, not human identity; keep explicit admission and physical
qualification. Same-origin transport, Origin/CSRF checks, quotas, expiry and
execution-time policy checks remain mandatory.

Acceptance: local bootstrap and automatically available invitation work without
any account system or external authentication calls. Operator access needs no
token and does not expire. Missing, wrong-role, expired, revoked and reused
guest capabilities are rejected at the guest boundary. A copied guest link does
not expose operator actions through guest routes or bypass device policy.

## Design constraints for module specifications

Components must be conceptually replaceable through narrow, explicit contracts.
The target is replacement without rewriting consumers, not runtime hot-swapping
or a plug-in framework.

### Interfaces and assumptions

Each provider specification must define its inputs, outputs, units,
capabilities, errors, state transitions, resource ownership and disposal. Where
operations are asynchronous, also define ordering, deadlines, cancellation,
stale completion and acknowledgement semantics. Distinguish accepted requests,
completed writes, verified device feedback and unknown physical state; never
collapse them into a generic success result.

Keep implementation details behind their owning boundary:

- Bridge/native handles, firmware commands and parameter mappings stay inside
  `device-control`. Consumers use its declared capabilities and verified
  semantics, not assumptions about every device or pattern.
- Remote wire schemas and validation rules are shared contract artifacts owned
  by `session-relay`, separate from its server implementation. Device contracts
  stay inside admin; versioned guest-to-admin IPC and remote messages never
  contain implementation objects, device handles or trusted execution guards.
- Host policy and motion authority do not depend on a UI framework or raw socket
  API. Guest UI and transport handlers cannot bypass server policy or the
  controller.
- Capability validation is isolated at the authentication boundary. Session
  logic consumes validated scope, role and expiry, not account records,
  arbitrary forwarded headers or a proxy-specific identity.

Require explicit units and declared capabilities rather than vendor-name checks,
guessed defaults or percentages assumed to have identical meanings. Capabilities
describe supported behavior; they do not grant permission. Missing required
capabilities or feedback prevent remote arming.

Use explicit composition at these boundaries. Start with one production
implementation per boundary and test doubles where needed. Do not add generic
factories, dynamic discovery or alternate transports merely to demonstrate
extensibility.

### Protocol and schema evolution

Version the wire envelope and message schemas explicitly, independently of
application release numbers. The wire contract must identify the applicable
schema unambiguously; the exact version representation belongs in its spec. Do
not require independent version counters for every internal interface.

Published schema versions retain their meaning. Do not change units, field
interpretation, required behavior or error semantics in place. Prefer compatible
additions, but retain exact validation: an added field requires a new supported
schema version or an explicitly negotiated extension with its own closed schema.
Unknown fields, messages and versions are rejected, not passed through as
arbitrary commands.

Establish a common supported protocol and required capabilities before
forwarding control traffic or arming. Fix that agreement for the session;
reconnection still requires a new guest session and owner approval; the
invitation is reusable unless cycled, ended by the operator or the admin
restarted. No common version or a missing required feature produces an explicit
incompatibility result and no motion authority. Never downgrade authorization,
freshness, Stop or required feedback guarantees to achieve compatibility.

The first implementation may support only version 1. Its specification must
define how future mixed-version participants either interoperate through a
common contract or are rejected safely. Do not assume simultaneous upgrades or
implement hypothetical compatibility layers now.

### Evidence of replaceability

Each boundary needs reusable conformance tests for its observable contract, not
tests tied only to one implementation's internals. Exercise host policy with a
fake bridge and test transport without real BLE or public hosting. Cover
supported and incompatible version combinations, unknown fields and extensions,
missing capabilities, stale callbacks and teardown.

A replacement that satisfies the same contract should require changes only
inside its adapter and composition wiring. Different semantics require an
explicit contract change, not a compatibility shim that pretends equivalence.
Software conformance does not replace hardware qualification: a new device
adapter or firmware revision must independently satisfy the physical gates.

## Scope and release boundaries

Retain the handoff's stock-firmware, supervised-use scope. Exclude firmware
replacement, streaming trajectories, multiple guests or devices, unattended
operation, persistent profiles, media, and automatic motion resumption.

Keep remote input disabled until the applicable controller blockers are resolved
on pinned revisions. Start remote integration against a simulated device;
hardware checks are a separate evidence gate.

Physical-use release requires verified independent stopping after service, OS or
bridge failure and BLE loss, plus an agreed physical stop-time bound. Guest
browser failure must be handled by the live service's expiry path. A service
lease, BLE write acknowledgement, or manual power cut does not establish an
automatic-stop guarantee. If stock firmware cannot meet the requirement, remain
at unloaded testing unless scope is explicitly revised. The proposed 750 ms
grant lifetime is not a verified physical stop bound.

## Approval and next phase

The user approved the capability map and design constraints on 13
September 2026. Specify for `device-control` is now active. No module
specification, implementation plan, task breakdown or physical implementation is
approved beyond the simulator-first scope approved on 14 September 2026.

| Module           | Specification                                          | Status                                                                                  |
| ---------------- | ------------------------------------------------------ | --------------------------------------------------------------------------------------- |
| `device-control` | [Service-owned device control](device-control/spec.md) | Private-admin ownership revision; spec review and bridge/hardware qualification pending |
| `session-relay`  | Not written                                            | Waiting for provider contracts                                                          |
| `host-authority` | Not written                                            | Waiting for dependencies                                                                |
| `guest-controls` | Not written                                            | Waiting for dependencies                                                                |
| `self-hosting`   | Not written                                            | Waiting for dependencies                                                                |

The revised spec selects separate .NET 10 guest/admin processes with one guest
browser, retaining one-box use, accountless links and physical release gates.
The bridge contract is defined independently of its native implementation.
Review the revised spec before planning.

## Downstream decisions from the project comparison

Use XToys' separation of admission, control permission and owner-only limits.
Use Sexync's private one-to-one flow, and SexCode's explicit
capability/handshake concepts. Keep this project's JSON protocol and
stock-firmware scope; do not import their custom firmware, media, accounts or
device-level watchdog claims. The
[device-control comparison](device-control/spec.md#ideas-adopted-from-existing-projects)
contains the source references and exclusions.

Use manual Send or opt-in Auto send with a visible-browser lease, owner approval
and mandatory owner rearming. Hold-to-run is removed. After disconnect, require
a new guest session, fresh approval and explicit arming; the same invitation can
be reused. Do not borrow room-resume behavior or remembered speed from another
product.

Approval, device selection and limits are trusted operator/service actions, not
permissions granted to the remote guest. Unauthenticated local management
controls exist only in the loopback admin service; no second browser runtime is
required. The policy spec must define the operator interaction; admission stays
paused until explicitly allowed. Prototype limits default to 100%, with optional
operator adjustments. **Apply limits** does not pause or arm control. Compatible
changes retain the current Ready epoch, sequence and accepted motion lease;
changes conflicting with accepted, active or issued motion reject atomically as
`limits-conflict`. Updated grants require the new policy version but do not
extend already accepted motion without fresh guest input. Stop, lock, faults and
expiry remain dominant. Do not reinterpret invitation redemption or a guest Send
action as owner approval.

SESSION-02 supersedes the earlier identity-proxy/Cloudflare Access login
proposal. The application authenticates temporary guest capabilities, not
accounts; the local operator surface is unauthenticated. A tunnel or reverse
proxy is optional remote transport only; it does not supply application
authority or bypass token validation.

Admin creates and owns sessions. Guest web forwards only scoped guest
operations; local operator access does not remove guest invitation/session
scopes. Remote guest exposure must preserve the same accountless guest
authorization boundary, and any third-party TLS/metadata trust boundary must be
disclosed. No tunnel, DNS, firewall or external-service configuration is
authorized by these requirements.

Save each module specification under `.docs/specs/<module-id>/` and link it from
this index. Review each specification before its plan, each plan before its
tasks, and each task set before implementation. Approval of this map alone
authorizes none of those later phases or physical-use release.
