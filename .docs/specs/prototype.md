# Simulator-first prototype

The user approved this direction on 14 September 2026. This document closes the
review gaps for that bounded prototype, without approving physical use. The
existing module IDs and DC-01 through DC-09 remain authoritative.

The revised UI uses three vertical sliders, manual Send and an opt-in Auto send
checkbox. Hold-to-run is removed. Valid browser invitations join automatically
on opening; invalid or cycled invitations display only an error. Operator
**Allow control** explicitly approves the pending guest and arms the session.
Default speed/depth limits are 100%; applying limits is optional. Opening the
application still cannot arm motion. The initial stopped controller is prepared
as part of the explicit Allow control action.

## Live operator limits

**Apply limits** does not pause control. A compatible changed policy preserves
Ready, generation, epoch, last accepted sequence, approved session and
serialized I/O. The accepted desired tuple, active observed tuple,
pending/in-flight writes and issued commands awaiting feedback must remain
within the proposed bounds. Otherwise reject the entire change as
`limits-conflict`, leaving the previous policy and control state intact; do not
clamp, silently pause or auto re-arm. The operator can explicitly Stop and wait
for stopped feedback before applying bounds incompatible with current motion.

Each successful changed policy increments `PolicyVersion`; identical limits are
a no-op. In Ready, superseded grants are cleared and a fresh-policy grant is
published immediately with the same epoch and sequence. This does not renew the
accepted authority: existing motion retains its original 750 ms issuance
deadline until new explicit guest input is accepted. Apply/status cannot keep
motion alive, and an overdue lease cannot be revived by Apply.

Old-policy input rejects as `policy-invalid`; current-policy input carrying a
superseded grant rejects as `grant-invalid`. Neither changes accepted intent or
sequence. Clients resynchronize policy/status without automatically submitting a
new tuple. The local limits endpoint remains `POST /api/v1/admin/limits`,
returning 204 on success or 409 with the rejection `code`. Initializing, paused,
locked and faulted controllers never arm from Apply. Stop, external lock,
session expiry and existing faults keep precedence.

## Existing simulator, not a second motion engine

Use the local sibling checkout `..\ossm-rs`, inspected at commit
`578161b2e78d8ca70f8909f0ec34b8ae6014cfda`. It is Apache-2.0 licensed. Its
`ossm-sim` application is an egui GUI that calls `ossm-motion` directly; it does
not expose a network/device endpoint.

`tools\ossm-sim-bridge` is a headless stdin/stdout adapter using the same
`ossm-motion` crate. It supplies an inert motor, timing and telemetry callbacks.
It must not open native device I/O, load GUI saved state, or modify the
`ossm-rs` checkout. The .NET admin owns this child process. The production
device bridge remains an unimplemented extension point.

The profile is **`ossm-rs-sim-v1`**, not the stock firmware profile. OSSM-RS
uses its own motion mapping and may retract when disabled. The adapter reports
enabled state, actual simulated movement and settings separately. Stopped
settings do not imply settled simulated movement; failure to settle stays
paused/faulted. Nothing observed here qualifies stock firmware, the physical
knob or physical stopping latency.

The adapter's fault controls simulate external parameter change, delayed write
completion, frozen feedback and disconnection. Only private admin can invoke
them. Command/sample correlation is explicit simulator evidence; it must not be
assumed to exist in a native adapter.

## Local bootstrap root of trust

The operator uses `aspire run` in their OS session. Aspire AppHost owns builds,
startup, port allocation, health and shutdown. It generates a 256-bit
guest-forwarding secret parameter shared only by admin and guest. Aspire
resource environment configuration replaces the earlier custom stdin launcher.

The authenticated local Aspire dashboard displays a plain local operator URL.
The operator UI/API and CLI are unauthenticated: no operator token or access
timeout is required. Admin always binds to `127.0.0.1` and retains exact
Host/Origin checks, including Origin checks for state changes and WebSockets.
Never expose or proxy the admin listener remotely. Guest forwarding and session
capability checks and leases remain enforced. The 15 September 2026 request
removes invitation expiry only; it does not extend guest control authority.
Admin rejects `Kestrel:Endpoints` and `PreferHostingUrls` overrides before
startup. Its listener uses only `OSSM:AdminPort`; hosting URL/port settings
cannot replace it, and endpoint configuration reload is disabled.

Treat Aspire development data and process environments as privileged.
Application code does not persist credentials, put them in arguments or log
them. Secret-parameter marking is not an OS isolation guarantee. Typed
`Microsoft.Extensions.Options` configuration uses DataAnnotations,
`IValidatableObject` and `ValidateOnStart`. Both UIs use Blazor Interactive
Server; the private `/api/v1/admin` minimal APIs and CLI call the same admin
application operations. No generic HTTP retry handler renews commands.

This first local prototype runs both processes as the invoking OS user.
Named-pipe current-user access, the forwarding credential, separate listeners
and operation allowlists protect the application boundary; **they do not isolate
arbitrary compromised code running as the same OS user**. Both web listeners are
loopback-only. Distinct OS process identities and native permissions are a
prerequisite to a hardened remote deployment, not a claim made by this
prototype.

Only one admin instance may own the prototype device. Startup remains disarmed.
Stop/shutdown targets the processes managed by Aspire; it does not kill
unrelated processes or rely on a browser callback.

## Invitation creation and full-origin links

The invitation is automatically available on the admin page, without a Create
action. It has no expiry and is reusable throughout the admin application's
lifetime. Keep it only in admin memory; restart changes the link. Invitation
retrieval does not issue device writes or change an active controller.

The invitation is separate from the active guest session. Join atomically
reserves the single guest slot and issues a fresh connection-bound proof; it
never admits a second pending or approved guest. Leave, transport loss, or guest
lease expiry revoke the old proof and follow Stop. The same invitation can then
join a new pending session. Pending approval still expires after 60 seconds;
approved sessions still expire after 30 minutes. Approval and explicit arming
remain required on every new session.

**Cycle link** atomically replaces the invitation, invalidates the old link,
guest proof, connection authority and grants, clears pending input and requests
serialized Stop. The replacement is available immediately, even while old I/O
settles. It cannot arm, clear an external lock or bypass outstanding-I/O checks.
Normal state is paused; existing locked/faulted states retain their protections.
The operator's **Stop and end session** action also atomically cycles the
invitation. `AdminOperations.End` invokes the same engine operation as Cycle,
not separate end/get steps. `POST /api/v1/admin/end` retains its 204 response;
the replacement is available through ordinary invitation retrieval immediately.
Internal/native `EndSession` and guest Leave retain their existing non-rotating
semantics; only the operator application action now rotates.

Native operations are `GetInvitation` and `CycleInvitation`. The local API uses
`GET /api/v1/admin/invitation` and `POST /api/v1/admin/invitation/cycle`. The
invitation response retains `sessionId`, `url`, `localOnly` and a null
`expiresAt`. Its session ID remains the link's ID until Cycle or operator end;
every admitted guest receives a separate session instance and fresh proof. Guest
wire schemas remain unchanged and strictly versioned.

The prior `CreateInvitation(attemptId, origin, creationScope)` and POST API
remain compatibility wrappers. Different attempts retrieve the current
invitation without replacing it; a repeated attempt is bound to its original
scope and link. After Cycle or operator end, old attempts reject rather than
resurrecting the old link. The bounded attempt ledger remains in memory; the
operator page uses native retrieval.

The full advertised guest origin is validated once: scheme, host and port; no
user information, path, query or fragment. Preserve `http` only for literal
loopback/localhost, otherwise require `https`. Append
`/join/<session-id>#invite=<secret>` without prepending another scheme. Local
links are labelled local-only. The prototype defaults to loopback and does not
configure remote routing, certificates or a tunnel.

## Guest transport and freshness

Guest web connects to a current-user restricted named pipe with a forwarding
credential and fresh connection ID. Frames are bounded, length-prefixed UTF-8
JSON. Admin binds that connection ID during handshake; per-request client fields
cannot replace it. The guest-forwarding contract has only `join`, `status`,
`input`, `stop` and `leave`, not management, device selection or simulator
controls.

The outer IPC envelope remains version 1. Its run-input schema is version 2: an
input includes `v = 2`, session/connection proof, a control epoch, policy
version, grant ID, strictly increasing positive safe-integer `seq`,
`run = true`, and a complete desired tuple. Version-1 hold inputs are rejected.
Closed schemas reject unknown fields, inappropriate payloads and unsupported
operations.

Admin creates an initial grant when a stopped session is explicitly armed, then
issues grants every **250 ms** while that epoch remains armed. Each grant
expires **750 ms from admin issuance**, irrespective of arrival. The same
unexpired grant can authorize increasing sequence numbers. Issuing a grant does
not renew motion: only accepted fresh guest input authorizes output, until that
grant's original deadline.

Input rate limiting uses an admin-monotonic token bucket: capacity **20**,
refill **10 tokens/second**, one token per `input` attempt after guest
authentication. Exhaustion rejects without extending authority. Status, Stop and
leave do not consume that motion bucket; all frames still have size/depth
limits. No heartbeat or status request renews motion.

Send commits the selected tuple. Auto send commits slider changes while enabled.
An active, visible browser renews only its last sent tuple at most every **100
ms**; unsent edits never enter that lease. At most one submission is
outstanding; auto changes coalesce to the newest selection. Stop,
focus/visibility loss, connection loss and new epochs disarm sending and clear
Auto send. Server/circuit timers read status only; they never manufacture run
input.

Selected values, transmitted values and device-confirmed current values are
distinct. An IPC acknowledgement is not device verification. Admin publishes
safe current settings/position/velocity from fresh device samples while keeping
raw device details private. `VerifiedSeq` advances only after the matching owned
device command is settled and positively observed. Missing/stale feedback
removes current telemetry instead of animating guessed movement. The run
indicator uses the reported enabled state or ongoing movement, so stroke
turnarounds do not flicker idle. This display rule does not change stopped-state
checks. Operator feedback is collapsed by default. A private, bounded live log
displays only explicitly tagged, safe application activity; exception details,
scopes, credentials and framework traffic are excluded.

Admin rechecks session, generation, lock, policy and grant at execution. IPC
loss, expiry and Stop revoke authority and clear pending input. A delayed issued
write remains owned until it settles; timeout is not cancellation. DC-09
external changes lock until explicit admin unlock, which leaves paused and
requires separate arming. Admin status explicitly reports pending I/O; readiness
is not inferred from zero settings alone. Guest authorization expiry closes its
client connection.

## Prototype acceptance

- Aspire starts separate admin and guest processes with the Rust simulator,
  reports availability and owns shutdown. System.CommandLine supplies the
  optional admin client, not a second orchestrator.
- Admin can initialize, set limits, generate a link in one click, approve the
  exact guest, arm, stop, revoke and unlock without accounts.
- Guest can join, view live state, send restricted settings and request Stop,
  but cannot reach privileged operations or the simulator directly.
- Guest/web loss expires authority. External changes lock; delayed and stale
  simulator behavior cannot reopen motion or create a second writer.
- .NET tests use xUnit v3, MTP v2 and FastMoq where appropriate; deterministic
  I/O and clock tests remain explicit. Rust uses its native test runner.
- Native BLE, physical stopping and remote-production privilege isolation remain
  excluded until separately qualified.
