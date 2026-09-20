# Control application, services and user interfaces

## Scope and ownership

This document owns higher-order application behavior. It consumes the
[reusable core](control-core.md) and must not move session transport, hosting or
UI requirements into the device library. Component names below describe logical
roles, not prescribed project or assembly names. Requirement terms are defined
in the [core contract](control-core.md#scope-and-ownership).

| Logical component                  | Ownership                                                                                  |
| ---------------------------------- | ------------------------------------------------------------------------------------------ |
| Control service                    | Authoritative invitations, sessions, policy, grants and the one controller/device instance |
| Operator web interface             | Local management presentation and operator commands                                        |
| Guest web interface                | Invitation redemption, restricted controls and scoped feedback                             |
| Control client                     | Versioned service calls and projections; no device execution                               |
| Shared web UI                      | Layout, widgets, notification styling and accessibility                                    |
| Command-line client                | Application commands through the control service, not a second writer                      |
| Application host and host defaults | Composition, process orchestration, configuration, local boundary protection and telemetry |

The control service, operator web host and guest web host must be separate
processes/listeners. Web hosts and command-line clients must not resolve a
controller or device implementation. The guest browser must communicate only
with the guest web host; server-side clients must use the control service.
Shared models carry data, not shared mutable authority.

## Application requirement IDs

The remaining CC identifiers refer to the
[core requirements](control-core.md#requirement-ownership). The following IDs
own application-specific obligations; their details appear once in the linked
sections.

| ID    | Owning application contract                                                                                                                                                |
| ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| CC-02 | [SESSION-01](#session-01-automatic-reusable-invitation): one memory-only, reusable, non-expiring invitation and side-effect-free retrieval.                                |
| CC-03 | [Admission and session lifetime](#admission-and-session-lifetime): one connection-bound guest, private Join proof and explicit approval for each session.                  |
| CC-04 | [Admission and session lifetime](#admission-and-session-lifetime): bounded pending/approved leases and revocation on expiry, leave or loss without invitation replacement. |
| CC-05 | [Invitation rotation](#invitation-rotation): atomic Cycle/operator end, replacement availability and stale-authority rejection.                                            |
| CC-07 | [Guest input and grant freshness](#guest-input-and-grant-freshness): authorization, versions, sequence, grants, rate limits and motion leases.                             |

## Deployment and trust

### DEP-01: Local one-box operation

The browser, all three service processes and device adapter must be able to run
on one supported computer. One-box operation must not require another server,
proxy, tunnel, public DNS, cloud identity or an internet connection after
installation. Runtime assets must be local. Local and remote guests must use the
same authorization, policy and core execution path.

The operator web host must bind exclusively to `127.0.0.1`, remain deliberately
unauthenticated and retain exact Host/Origin checks and applicable CSRF
protection for mutations and WebSockets. It must not be remotely exposed or
proxied. Operator access must require neither a token nor an access timeout. The
listener must use only `OSSM:Admin:AdminPort`, reject `Kestrel` and
`PreferHostingUrls` configuration before startup, ignore hosting URL/port
overrides, and disable endpoint-configuration reload.

HTTP/WS may be used only at the permitted loopback boundary. Remote guest and
service connections must use explicitly configured HTTPS/WSS, with certificate
validation intact. Deployment must not automatically bind all interfaces, trust
forwarded headers, change firewall rules or start a tunnel to make a link work.
Containers are optional; when authorized, rootless Podman must not require
privileged mode or a container-engine socket for device access.

### Local bootstrap root of trust

The operator must use `aspire run`; the application host must own build,
startup, port allocation, health and shutdown. In simulator mode it must prepare
the native executable for the service-owned device adapter, which owns the only
simulator child. Physical mode must not build or launch that child or require
Rust tooling. Host orchestration and service DI must use the same startup
selection; the
[adapter design](device-control/adapters.md#composition-and-ownership) details
that composition. There must be no custom service launcher. The application host
must generate distinct 256-bit service keys for the operator and guest web hosts
and supply them through resource configuration. Keys authenticate hosts, not
humans or guest authority. The authenticated local dashboard must display the
plain local operator URL.

Typed configuration must use options, DataAnnotations, cross-field validation
where needed, `ValidateDataAnnotations()` and `ValidateOnStart()`. Application
code must not persist credentials, put them in process arguments or log them.
Development data and process environments are privileged; secret-parameter
marking does not establish OS isolation. Shared host defaults must own common
startup and OpenTelemetry configuration.

Processes running as one OS user are not isolated from arbitrary compromised
code under that identity. A local process can reach the unauthenticated operator
listener. Hardened remote deployment must establish distinct process identities
and native permissions; service keys and listener separation alone do not prove
that isolation or human intent. A compromised guest host may forge input within
a capability it possesses, but service-side policy and grants must still bound
it.

The service must exclusively own the composed controller and device lifetime.
Shutdown must target only its managed processes, not unrelated processes or a
browser callback. Physical-use limits remain in the
[device qualification contract](device-control/spec.md#physical-qualification).

### SESSION-02: Accountless access

The application must not require accounts, registration, login, passwords, a
user database, an external identity provider or stored personal identities.
Operator denotes a local management role; guest denotes temporary scoped
capability possession, not a verified identity.

Only the control service may create guest capabilities. Session secrets must use
independently generated 256-bit CSPRNG values, fixed expiries and
server-enforced scopes. Guest credentials must not authorize invitation
creation, policy changes, device selection, arming, unlocking or management. The
guest web host must not access service bootstrap/secret storage or native device
permissions. Service authentication must not replace guest validation.

Invitation secrets must travel in URL fragments, be removed promptly on
redemption and never enter query strings, request logs or retained browser
storage. Subsequent guest credentials must remain in memory. Validation must
retain hashes only, except for the bounded in-memory invitation result needed
for repeated retrieval. Telemetry, public assets and source must contain no
secrets. Restart must discard guest authority.

## Invitations and sessions

### SESSION-01: Automatic reusable invitation

After advertised-origin validation, the service must create one high-entropy,
non-expiring invitation in memory for its lifetime. The operator interface must
show its complete URL automatically, without a creation click, manually entered
session ID or token assembly. It must provide Copy and retain a selectable URL
when clipboard access fails. Retrieval and compatible creation retries must be
side-effect-free: no device writes, authority changes or multiple invitations.

`GuestAdvertisedOrigin` must be one startup-validated canonical origin
containing scheme, host and port only. User information, paths, queries and
fragments must reject. Only literal `localhost`, `127.0.0.1` and `[::1]` may use
`http`; all other hosts must use `https`. The configured origin must match the
guest listener's advertised origin before invitation publication. Missing or
mismatched configuration must produce unavailable status, not a fallback link.

The URL must be `<guest-origin>/join/<sid>#invite=<secret>`, preserving scheme
and port. It must not derive from a request Host header or operator origin.
Loopback URLs must be labelled **Local only - this computer**. Remote URLs must
require already configured reachable HTTPS exposure. The UI must explain missing
setup rather than present localhost as remotely shareable.

The service must own one serialized slot containing the invitation generation
and at most one guest lease in `vacant`, `pending` or `approved` state. A
compatibility attempt ledger may map retrieval attempts to their invitation
generation but must not create/replace invitations or resurrect an invalidated
generation. Compatible retries must return the current snapshot or reject after
rotation/restart. Ordinary reads and reloads must retain the same URL.

`GetInvitation` and `CycleInvitation` are application operations. The result
must retain `sessionId`, `url`, `localOnly` and null `expiresAt`; the link's
session ID remains stable until rotation, while each admitted guest receives a
separate session instance. No REST wrapper is required.

### Admission and session lifetime

Join must atomically validate the current invitation generation/proof and move a
vacant slot to pending. Concurrent joins must admit exactly one connection. Join
must return only a private connection-bound bootstrap admission proof, not a
snapshot or grant. Every guest operation must validate the proof, current
session, invitation generation and authoritative connection binding.

Unapproved sessions must expire after 60 seconds; approved sessions after 30
minutes. Leave, expiry and detected transport loss must revoke the guest
proof/grants, clear input, request core Stop and return the slot to vacant
without replacing the invitation. Authorization expiry must close the guest
client connection. A new session must require fresh proof, approval and arming.
Internal end/revocation must not wait for a guest acknowledgement. Internal
session end has the same non-rotating semantics as guest Leave.

### Invitation rotation

Cycle and the operator's **Stop and end session** must use one atomic operation:
advance invitation generation, revoke the old link/proof/connection/grants,
clear pending input and stale authority-restoring requests, request serialized
core Stop, and publish the replacement invitation. They must not use separate
end/read steps. The replacement must be available while old I/O settles, but
outstanding I/O must still prevent arming. Rotation must leave ordinary control
paused and preserve faults/locks. Late operations and compatibility retries must
not affect a replacement guest or revive the old invitation. Restart must also
replace the invitation.

### Approval and control authority

The service must approve the exact admitted guest before arming. **Allow
control** must explicitly perform approval and prepare/arm the stopped session
only when limits and fresh supported-profile feedback satisfy CC-01. Opening the
application, retrieving/redeeming an invitation or sending guest input must not
constitute approval. Default speed/depth limits must be 100%; changing them must
be optional. Physical profiles remain subject to qualification.

Approval, arming and unlock must supply the expected current authority revision
required by DC-02. Guest operations must not unlock. Successful trusted operator
unlock must use DC-09 and leave paused; a separate arm and new guest Send must
precede motion. No new invitation or session may bypass a core fault or lock.

### Live operator limits

The service must apply limits using CC-06/DC-03. Compatible changes must
preserve the approved session, epoch and last accepted sequence. A changed
policy must increment `PolicyVersion`, invalidate prior-policy grants and
immediately publish a new-policy grant with the same epoch. This publication
must not extend the accepted motion lease's original deadline. Identical limits
must be a no-op.

Conflicts must reject atomically as `limits-conflict`. Old-policy inputs must
reject as `policy-invalid`; a superseded grant under current policy must reject
as `grant-invalid`. Rejections must not change intent or sequence. Clients must
resynchronize status without dispatching unsent settings. A stale-policy retry
may retain explicitly sent settings, but must not extend a motion lease through
status refresh. Operators may Stop and wait for stopped feedback before applying
otherwise conflicting limits. Apply must not pause, rearm or revive expired
authority. The hub must return acknowledgement-only success or rejection.

## Service transport

Application clients must use `/hubs/control/v1` over HTTPS/WSS with separate
web-host service keys. The service must independently validate guest proofs;
guest-side parsing/quotas are additional checks, not an authorization boundary.
Allowed guest operations are Join, scoped status, input, Stop and leave. The
endpoint must not expose raw commands, device enumeration, arbitrary method
invocation, position streaming, firmware actions or simulator fault controls.
SignalR is the only current control transport. Future REST-compatible operation
semantics must not introduce a REST, named-pipe or alternate execution path now.

Logical envelopes must use closed, explicit JSON schemas with no CLR type-name
dispatch. Each inbound/outbound logical envelope, including one bounded log
batch, must be at most 16 KiB of serialized JSON, excluding transport framing.
Unknown/duplicate fields, inappropriate payloads, operations and unsupported
versions must reject before control handling.

Ordinary replies must acknowledge acceptance/rejection only. Initial and live
state must share one ordered server-push path, with an in-session cursor for
state/log ordering, not a replay/resume token. A timer pass that leaves the
guest-visible projection unchanged must not advance publication revision or
queue another delivery. Disconnect must not replay, reconnect or rearm; clients
must begin a new admission flow.

Published schema versions must retain their units, interpretation and error
semantics. Added fields require a new supported version or an explicitly
negotiated extension with a closed schema. Participants must establish a common
contract and required capabilities before arming; incompatibility must fail
without weakening authorization, freshness, Stop or feedback guarantees. There
must be no speculative compatibility framework or simultaneous-upgrade
assumption.

### Guest input and grant freshness

Version-2 input must contain `v = 2`, session/connection proof, control epoch,
policy version, grant ID and grant sequence, strictly increasing positive
safe-integer `seq`, `run = true`, and a complete valid tuple. The hub-bound
connection identity must be authoritative. Version-1 hold inputs, nonincreasing
input sequences, replay and expired/superseded/cross-session/epoch grants must
reject without device writes or lease renewal.

The service must issue an initial grant on explicit arming, then one every 250
ms while armed. Each grant must contain session, connection, epoch, policy
version, strictly increasing grant sequence, issuance timestamp and expiry 750
ms after service issuance, irrespective of arrival. One unexpired grant may
authorize increasing input sequences. Accepted input must supply its original
grant deadline and sequence to the core motion guard. Grant publication alone
must not renew a lease or dispatch motion.

After authentication, the service must accept at most 10 run-input attempts per
connection/epoch in any rolling monotonic one-second window. Over-limit attempts
must not advance sequence, refresh authority, replace pending work or reach the
controller. Status, Stop and leave must not consume that capacity. All messages
still have the envelope-size bound.

The recorded 10-attempt rolling-window rule and 20-capacity bucket are not
equivalent. This remains an unresolved discrepancy, not permission to relax the
rolling-window rule or silently choose a replacement algorithm.

The service must recheck session, policy and grant through the core execution
guard. Neither server/circuit timers, status, heartbeat, ping/pong, retained UI
state nor a connection probe may renew motion authority. Detected loss must
invoke Stop; undetected stalls remain bounded by the accepted grant deadline.
The 1-second/3-second loss thresholds are detection tuning only, not real-time
or physical-stop guarantees. Restart must discard sessions, proofs, sequences
and grants. Cached client state must not restore them.

## User interfaces

The web interfaces must use Blazor Interactive Server and share one page layout,
control sizing, spacing and notification components. UI widgets and browser
state must not authorize motion. The command-line client must use
`System.CommandLine` and the service contract, not another device writer or
launcher. Guest credentials must not appear in command arguments or diagnostics.

### Sending and feedback

The guest interface must expose three vertical sliders, Send, opt-in Auto send
and Stop. Valid invitations must Join automatically; invalid/cycled invitations
must show only the standard error alert without a redundant error heading. Send
commits selected settings; Auto send commits changes while enabled. The active,
visible browser may renew only the last explicitly sent tuple, at most every 100
ms. Unsent drafts must never enter a lease. Only one submission may be
outstanding; changes must coalesce to the newest selection. Stop,
focus/visibility loss, connection loss and a new epoch must disarm sending and
clear Auto send.

The service projection must carry core observation time/freshness and preserve
selected, sent and observed values. `VerifiedSeq`, where retained, must
associate input with settled writes and matching live observations, not firmware
IDs or exclusive causation. The UI must label missing data unavailable and stale
data last-known; successful writes and liveness probes must not be displayed as
fresh feedback. Reported speed zero means operationally stopped; positive means
running. The display must not require Enabled/Moving or claim physical
settlement. Simulator velocity may be displayed directly; stock
estimates/previews must be labelled and must not animate stale data as live
motion.

Operator live feedback must be collapsed by default. A private live log must
show only bounded, explicitly tagged safe application events, without exception
details, scopes, credentials or framework traffic. Connection failure notices
must take precedence. On guest connection loss, the standard danger alert must
say **Connection lost. Open the invitation link again.**; the remaining guest
interface must be grayscale and paused guidance hidden. Framework-managed
connection UI must remain outside interactive-route DOM.

### Operator presentation requirements

| ID     | Requirement                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| OUI-01 | Collapsed feedback must be labelled **Live feedback**, without a redundant connection word. The operator action card must retain **Controls** as its heading, not a transient readiness state.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| OUI-02 | Above the narrow breakpoint, Controls and **Invite a guest** must be adjacent equal-width, equal-height cards. Narrow layouts must preserve source/keyboard order while stacking without horizontal overflow. The page heading must align with the widgets' left edge.                                                                                                                                                                                                                                                                                                                                                                                                                             |
| OUI-03 | The shared layout's site label must be 1.4rem. The SIMULATOR indicator, when present, must align at the page header's top-right with the site label/widgets, not appear in the Controls heading. That heading's guest-session indicator must be right-aligned; pending device I/O must add a preceding large recording-red dot with accessible text/tooltip **Waiting for the device to settle.**, not a normal-flow status message.                                                                                                                                                                                                                                                               |
| OUI-04 | **Session limits** must span the operator grid and retain top-right dirty status. All operator/guest card headings must render at 1.1rem without changing semantic levels; the limits landmark must remain h2.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| OUI-05 | Speed/depth ranges must share one right-aligned **Apply limits** action and one atomic policy operation. They must omit redundant selected/applied text rows. While draft and applied values differ, a primary/selected-colour phantom dot must mark the applied track position and disappear after successful application makes them equal. Status refresh must preserve unsent edits.                                                                                                                                                                                                                                                                                                            |
| OUI-06 | Both interfaces must share the page-header/content template and inline notification component. Application notifications must be full-content-width above widgets without a fixed wrapper, popup, modal or outer widget border. An actionable pending guest must show persistent warning/gold **A guest is waiting. Click Allow control to enable control.** and green attention treatment on enabled Allow control; the notice must clear when no longer actionable. The shared package must supply the animated green gradient/glow, static reduced-motion treatment and disabled grayscale. Framework-owned connection/reconnection/fatal notices must remain the only fixed top-right notices. |

### Guest presentation requirements

| ID     | Requirement                                                                                                                                                                                                                                                                                       |
| ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GUI-01 | The shared main region must not have an initial visible focus outline. The status card must be headed **Live Status**, with state and applicable SIMULATOR badges in its right-aligned heading group, without numeric position, velocity or source-caption rows.                                  |
| GUI-02 | Non-wrapping **Auto send** must immediately precede green **Send**, with Auto send, Send and Stop right-aligned together. Paused status must use warning/gold without duplicate operator-disabled guidance. Terminated sessions must say **Session ended. Open the invite link again to rejoin.** |
| GUI-03 | The invitation-required page must use the same page layout and standard inline notification as the interactive guest and operator routes, with normal-flow full-width content and no fixed notification container.                                                                                |

Every button must have an accessible name and useful tooltip; icon-only buttons
must have explicit labels. Adjacent inputs/buttons must match heights. Waiting
must use warning/gold; connection/success actions green; **Stop and end
session** warning/yellow. Disabled buttons must be grayscale. Copied text and
success styling must share a timer. Shared widget changes must be made in the
owning UI package and consumed with verified package checksum/CSP hashes, not
copied assets.

## Application conformance

Service-boundary tests must prove role separation, proof/connection binding,
bounded messages, atomic invitation rotation, policy/grant expiry, stale
authority rejection and loss behavior against a fake or simulated device. They
must exercise core safeguards by reference, not redefine them in UI tests. Wrong
Host and missing/foreign mutation Origin must reject at the local boundary.

Focused component/browser checks must prove OUI/GUI behavior, sending semantics,
freshness labels and one-box operation without external networking. Command-line
checks must cover help, invalid-input exit codes, redirected output and
cancellation. Simulator fault injection must remain operator/test-only. Software
success must not be presented as physical qualification.
