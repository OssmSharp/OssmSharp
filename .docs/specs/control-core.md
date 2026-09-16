# Simulator-first ControlCore

The user approved this direction on 14 September 2026. This document closes the
review gaps for that bounded current implementation, without approving physical
use. The existing module IDs and DC-01 through DC-09 remain authoritative.

The revised UI uses three vertical sliders, manual Send and an opt-in Auto send
checkbox. Hold-to-run is removed. Valid browser invitations join automatically
on opening; invalid or cycled invitations display only an error. Operator
**Allow control** explicitly approves the pending guest and arms the session.
Default speed/depth limits are 100%; applying limits is optional. Opening the
application still cannot arm motion. The initial stopped controller is prepared
as part of the explicit Allow control action.

## ControlCore requirements

These requirements describe the simulator-first control authority currently
implemented by ControlCore. They are derived from executable behavior and do not
qualify physical-device safety or production deployment.

| ID    | Requirement                                                                                                                                                                                                                                                                                                                                                                                                | Acceptance evidence                                                                                                                                                                                                  |
| ----- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| CC-01 | The control authority shall begin disarmed with a stopped desired state. It shall arm only after an admitted guest has been explicitly approved, local limits allow control, and fresh feedback confirms a supported simulator profile is stopped.                                                                                                                                                         | An attempt to arm before any prerequisite is met is rejected; a successful arm publishes an initial authority grant while desired speed remains zero.                                                                |
| CC-02 | The control authority shall keep one reusable, non-expiring invitation in memory for its lifetime. Reading or retrying compatible creation of that invitation shall not alter active control.                                                                                                                                                                                                              | The same invitation is returned until it is cycled; it has no expiry and no retrieval operation changes the snapshot.                                                                                                |
| CC-03 | The control authority shall admit at most one guest connection for an invitation at a time. Admission shall require the invitation proof and create connection-bound guest proof; every new guest session shall require explicit approval and arming.                                                                                                                                                      | Concurrent joins yield exactly one admitted connection; wrong, malformed, stale, or connection-mismatched proof cannot read status, send input, stop, or leave.                                                      |
| CC-04 | The control authority shall expire an unapproved guest session after 60 seconds and an approved session after 30 minutes. Expiry, guest leave, and transport loss shall revoke guest authority and request Stop without replacing the reusable invitation.                                                                                                                                                 | Expired or disconnected guests cannot use the prior proof; the same invitation can admit a fresh pending guest after cleanup.                                                                                        |
| CC-05 | The control authority shall allow the operator to cycle the invitation or end the operator session. Either action shall immediately revoke the prior link, guest proof, connection, grants, and pending input; request serialized Stop; publish the replacement invitation; and never arm or clear a fault or external-control lock.                                                                       | Old guest operations reject after Cycle or operator end, pending motion does not dispatch, and a new invitation is available while outstanding I/O settles.                                                          |
| CC-06 | The control authority shall enforce an all-or-nothing local speed and depth policy. Compatible policy changes shall preserve current safe authority, intent, and lease deadlines while invalidating prior-policy grants; conflicting changes shall reject without modifying policy or control state; an identical change shall be a no-op.                                                                 | A compatible policy change publishes a fresh-policy grant without rearming; stale policy or grant input rejects; any conflict with accepted, observed, pending, in-flight, or unconfirmed motion rejects atomically. |
| CC-07 | The control authority shall authorize motion only from an authenticated admitted guest using a current protocol version, epoch, policy version, grant, strictly increasing sequence, complete valid tuple, and available rate-limit capacity. Grants shall expire 750 milliseconds after issuance; renewal shall be driven only by explicit valid input, not status, policy updates, or grant publication. | Invalid inputs cause no device write or lease renewal; the eleventh authenticated run-input attempt in any rolling one-second admin-monotonic window rejects; an expired lease pauses control and requests Stop. |
| CC-08 | The control authority shall expose an immutable state view. Guest-visible state shall include only session-scoped authority and live position/velocity, and shall withhold raw device details and connection identifiers. Device settings shall remain unverified until matching fresh feedback arrives; stale feedback shall not qualify control.                                                         | Accepted input is distinct from verified input; guest status omits raw device data; stale or unsupported feedback prevents arming and clears guest-visible live movement.                                            |
| CC-09 | The control authority shall serialize device operations, keep only the newest replaceable motion intent, and give Stop precedence over input. It shall not dispatch a later motion command after Stop, expiry, lockout, fault, disposal, or a missed execution-time authorization check.                                                                                                                   | Rapid input produces bounded pending work; a delayed write settles before serialized Stop; a pending envelope transition writes zero before later speed and rechecks authority between steps.                        |
| CC-10 | The control authority shall latch an external-control lock when fresh device feedback reports an unowned change to a controlled setting. Locking shall revoke authority, clear pending input, request Stop, and remain in effect until an explicit operator unlock using the current lock revision; unlock shall leave the controller paused.                                                              | Unowned changes during rest or in-flight writes lock control; stale unlock revisions fail; reconnect, invitation reuse, policy edits, recovery, and late input cannot clear the lock or restore motion.              |
| CC-11 | The control authority shall fail closed on device-operation timeout, bridge failure, unsupported feedback, or disposal. It shall retain exclusive ownership until outstanding I/O settles, request Stop where possible, and expose a non-ready state without invitation secrets.                                                                                                                           | Late completion cannot revive motion; failed bridge work latches a safe reason and only Stop is queued; disposal revokes the invitation and unsubscribes device feedback.                                            |

Detailed invitation, policy, transport, and safety behavior follows in the
sections below; they refine these requirements and remain the source for
endpoint and protocol specifics.

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
Admin rejects all `Kestrel` and `PreferHostingUrls` configuration before
startup. Its listener uses only `OSSM:AdminPort`; hosting URL/port settings
cannot replace it, and endpoint configuration reload is disabled.

Treat Aspire development data and process environments as privileged.
Application code does not persist credentials, put them in arguments or log
them. Secret-parameter marking is not an OS isolation guarantee. Typed
`Microsoft.Extensions.Options` configuration uses DataAnnotations,
`IValidatableObject` and `ValidateOnStart`. Both UIs use Blazor Interactive
Server; the private `/api/v1/admin` minimal APIs and CLI call the same admin
application operations. No generic HTTP retry handler renews commands.

This first local ControlCore deployment runs both processes as the invoking OS
user. Named-pipe current-user access, the forwarding credential, separate
listeners and operation allowlists protect the application boundary; **they do
not isolate arbitrary compromised code running as the same OS user**. Both web
listeners are loopback-only. Distinct OS process identities and native
permissions are a prerequisite to a hardened remote deployment, not a claim made
by this ControlCore.

Only one admin instance may own the ControlCore device. Startup remains
disarmed. Stop/shutdown targets the processes managed by Aspire; it does not
kill unrelated processes or rely on a browser callback.

## Invitation creation and full-origin links

The invitation is automatically available on the admin page, without a Create
action. It has no expiry and is reusable throughout the admin application's
lifetime. Keep it only in admin memory; restart changes the link. Invitation
retrieval does not issue device writes or change an active controller.

The control authority owns one serialized invitation/session slot containing
the current invitation generation and at most one connection-bound guest lease
in `vacant`, `pending` or `approved` state. Startup creates the initial
invitation generation only after advertised-origin validation succeeds.
Retrieval reads that immutable generation without a state transition. Join
compares the current generation and invitation proof, then atomically moves a
vacant slot to pending. Leave, guest transport loss and session expiry revoke
only the guest lease and return the slot to vacant; they retain the invitation.

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
remain compatibility wrappers. Their bounded in-memory ledger maps each
attempt identifier to the invitation generation it retrieved; it never creates
or replaces an invitation. Different attempts retrieve the same immutable
current invitation without affecting a guest lease. A repeated attempt returns
that original generation only while it remains current. After Cycle or operator
end, old attempts reject rather than resurrecting the old link. The operator
page uses native retrieval.

`GuestAdvertisedOrigin` is startup-validated typed configuration, not a request
Host header. It is one canonical absolute origin containing scheme, host and
port only: user information, path, query and fragment are invalid. Preserve
`http` only for the literal hosts `localhost`, `127.0.0.1` and `[::1]`;
otherwise require `https`. The admin validates that the configured guest
listener advertises this exact canonical origin before it publishes the initial
invitation. Failure leaves invitation retrieval explicitly unavailable rather
than returning a guessed or fallback link. Append
`/join/<session-id>#invite=<secret>` without prepending another scheme. Local
links are labelled local-only. ControlCore defaults to loopback and does not
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
version, grant ID and grant sequence, strictly increasing positive safe-integer
`seq`, `run = true`, and a complete desired tuple. The handshake-bound
connection identity is authoritative; per-frame client fields cannot substitute
for it. Admin rejects a sequence less than or equal to the connection and
epoch's last accepted sequence, an expired/superseded grant, a
cross-session/epoch grant or replay. Version-1 hold inputs are rejected. Closed
schemas reject unknown fields, inappropriate payloads and unsupported
operations.

Admin creates an initial grant when a stopped session is explicitly armed, then
issues grants every **250 ms** while that epoch remains armed. Each grant
records the session, handshake-bound connection, epoch, policy version, strictly
increasing grant sequence, issuance timestamp and expiry **750 ms from admin
issuance**, irrespective of arrival. The same unexpired grant can authorize
increasing sequence numbers. The controller copies the accepted input's grant
deadline and input sequence into its motion lease. Issuing or publishing a
later grant does not replace that lease, renew motion or dispatch a write:
only accepted fresh guest input authorizes output, until that input grant's
original deadline.

AdminService applies a rolling admin-monotonic one-second input window after
guest authentication. It accepts no more than **10** run-input attempts for a
connection and epoch in that window. An over-limit attempt rejects without
advancing the accepted sequence, refreshing a grant, replacing pending work or
reaching the controller. Status, Stop and leave do not consume that input
capacity; all frames still have size/depth limits. Browser Auto send may
coalesce input but cannot relax this service limit. No heartbeat or status
request renews motion.

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

## Current implementation acceptance

- Aspire starts separate admin and guest processes with the Rust simulator,
  reports availability and owns shutdown. System.CommandLine supplies the
  optional admin client, not a second orchestrator.
- Admin can initialize, set limits, retrieve the automatic reusable invitation,
  approve the exact guest, arm, stop, revoke and unlock without accounts.
- Guest can join, view live state, send restricted settings and request Stop,
  but cannot reach privileged operations or the simulator directly.
- Guest/web loss expires authority. External changes lock; delayed and stale
  simulator behavior cannot reopen motion or create a second writer.
- .NET tests use xUnit v3, MTP v2 and FastMoq where appropriate; deterministic
  I/O and clock tests remain explicit. Rust uses its native test runner.
- Native BLE, physical stopping and remote-production privilege isolation remain
  excluded until separately qualified.
