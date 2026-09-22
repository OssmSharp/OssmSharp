# Web-control implementation plan

Date: 22 September 2026

Status: Planned, not implemented. This is the authoritative task list for the
[web-control design](../specs/web-control-design.md). Saving this plan does not
establish a passing build, completed implementation or physical qualification.

## Goal and scope

Separate the SignalR host into `OSSMDotNet.Api`, make
`OSSMDotNet.ControlService` the transport-neutral application library, and keep
device execution independently usable in ControlCore. AdminWeb and GuestWeb
remain separate Blazor hosts using the existing ControlClient contract. Api
hosts the library and sole device owner; it is not a fourth application service.

No REST surface, new dependency, physical-device work, broad validation cleanup
or unrelated naming refactor is included. Preserve the device-ownership lease's
existing exclusion scope through the host rename.

## Confirmed decisions

- The requester selected coordinated updates to repository .NET consumers,
  without source/binary compatibility shims for moved types. Preserve SignalR
  methods, wire representations and error semantics during extraction.
- The requester selected **10 authenticated run-input attempts in any rolling
  monotonic second**, replacing the existing 20-capacity bucket as an explicit
  behavior correction. Preserve authenticated rejected-attempt accounting and
  the Stop/Leave exemptions.
- Reuse the existing device contracts, application operation seam, client,
  shared widgets and tests. Do not introduce a command bus, duplicate authority,
  generic transport framework or replacement device bridge.

## Dependencies and current evidence

The target call path is web hosts → ControlClient → Api → ControlService →
ControlCore → device abstractions. Api selects the concrete adapter at its
composition root. Models remains shared immutable data; operation interfaces
belong with their operation owner. Neither web host resolves application
authority or a device implementation.

Inspection found unresolved legacy `IDeviceBridge`/`SimulatorProcessBridge`
consumers alongside the newer `IOssmDevice` implementations. This is a
source/dependency inconsistency, not a verified build failure. Establish the
affected baseline first rather than assume the extraction begins from green.

Task 1 precedes tasks 2 and 3; both precede task 4. Task 5 follows task 4,
and task 6 follows task 5. Tasks 2 and 3 currently touch the same engine and
tests, so perform them sequentially unless file ownership is separated first.
Library conversion and host extraction may share one coordinated working
increment; intermediate states need not be independently deployable.

## Tasks

### 1. Establish the device baseline

- **Owner/scope:** Core and device composition; `PrototypeEngine.cs`,
  `DeviceLoop.cs`, service registrations and affected project references.
- **Work:** Reconcile obsolete bridge consumers with the existing `IOssmDevice`
  adapters, including feedback, lifetime and privileged simulator controls.
  Follow the [adapter design](../specs/device-control/adapters.md); do not
  recreate the obsolete bridge or broaden into physical BLE integration.
- **Acceptance:** Affected projects build against the supported simulator path.
  One writer retains unsettled I/O ownership; fresh device observations drive
  readiness; Stop cannot race a second write.
- **Verification:** Establish the focused build baseline, then use deterministic
  core/device tests and the affected simulator integration checks. For changed
  behavior, first prove a focused regression fails for the intended reason.

### 2. Correct the rolling input limit

- **Depends on:** Task 1.
- **Owner/scope:** Application policy currently in `PrototypeEngine.cs`;
  `PrototypeEngineTests.cs` and `LimitsPolicyTests.cs`.
- **Work:** Prove an eleventh authenticated attempt within a rolling second is
  rejected, then replace the token bucket. Update the existing capacity-20 test
  to the approved contract rather than remove the test.
- **Acceptance:** Boundary and burst cases obey the rolling limit using monotonic
  time. Authenticated invalid attempts still consume capacity; rejected excess
  work cannot advance sequence, renew authority or replace pending work. Status,
  Stop and Leave remain exempt.
- **Verification:** Controlled-clock tests around the rolling boundary, idle
  bursts, wall-clock jumps, rejected attempts and unchanged motion deadlines.
  Keep this behavior correction distinct from project/file moves.

### 3. Establish atomic core operations

- **Depends on:** Task 1; coordinate shared engine edits with task 2.
- **Owner/scope:** ControlCore controller contracts and `PrototypeEngine.cs`;
  the affected core tests, particularly `LimitsPolicyTests.cs`.
- **Work:** Expose arm/limit operations that atomically check and commit
  execution-side preconditions. Do not replace them with snapshot reads followed
  by separate writes, or copy pending device state into the application layer.
- **Acceptance:** Rejection leaves intent and limits unchanged. Stop wins,
  delayed writes retain ownership, and recovery leaves paused. The execution
  boundary can be exercised without creating a guest session. Application-to-core
  lock acquisition stays one-way; callbacks/publication occur outside locks.
- **Verification:** Deterministic tests for in-flight and awaiting-feedback
  conflicts, arm preconditions, stale revisions, Stop races and logical timeout
  without I/O settlement. Preserve DC-01 through DC-09 by reference.

### 4. Extract application authority into ControlService

- **Depends on:** Tasks 2 and 3.
- **Owner/scope:** `ControlAuthority.cs`, `ControlOperations.cs`,
  `GuestControlOperations.cs`, session/grant state currently in the engine, and
  `Models/IControlOperations.cs` with its repository consumers.
- **Work:** Move invitations, admissions, policy versions, grants, sequences,
  application deadlines and scoped projections into one serialized authority.
  Separate guest operations from the SignalR registry and concrete simulator.
  Move operation interfaces to their owner without compatibility shims.
- **Acceptance:** Application authority is testable without ASP.NET or SignalR;
  ControlCore needs no remote session. Atomic core rejection leaves associated
  policy/grant state unchanged. No competing authority or execution path remains.
- **Verification:** Reuse/move `InvitationLifecycleTests`, applicable
  `LimitsPolicyTests`, `ControlAuthorityTests`, `ControlAuthorityRevisionTests`
  and `GuestControlOperationsTests`. Prove exact admission, stale-proof rejection,
  rotation during unsettled I/O, expiry, and policy changes without lease extension.

### 5. Extract Api and cut over composition

- **Depends on:** Task 4; coordinate library/project conversion in this increment.
- **Owner/scope:** New `OSSMDotNet.Api`; existing `Program.cs`, authentication,
  listeners, hub, connection registry, push dispatcher and ownership lifetime;
  AppHost, solution/project references and friend assemblies.
- **Work:** Move hosting and delivery to Api; make ControlService a class library.
  Keep application invalidation decisions in the library, not inferred again
  from UI state strings in delivery code. Preserve configuration keys, hub path
  and circuit-scoped clients. Move tests to the assembly that owns their subject.
- **Acceptance:** Api owns one library/controller/device lifetime. Three service
  processes remain. A competing old/new host cannot acquire device ownership;
  the lease lasts through managed shutdown. Guest and operator service roles,
  proof binding, bounded push and cancellation/unknown-outcome semantics remain.
- **Verification:** Focused auth, protocol, registry and dispatcher tests; real
  local SignalR contract/push/loss checks; listener-override and competing-owner
  evidence. Existing same-process semaphore/options tests alone do not establish
  cross-process exclusion or actual socket exposure. Keep unit and integration
  projects named after their owning assembly and reuse shared fakes.

### 6. Verify consumers and complete documentation

- **Depends on:** Task 5.
- **Owner/scope:** ControlClient, AdminWeb/GuestWeb tests, focused browser checks,
  AppHost composition and `.docs/diagrams/architecture.html`.
- **Acceptance:** Invite → approve → Send → Stop works through both web hosts.
  Draft edits survive refresh without being sent; loss clears authority and
  requires fresh admission. Restart, reconnect and new invitations never arm
  motion. CLI consumers retain the compatible service path, not a second writer.
- **Verification:** `HomePageBehaviorTests`, `JoinPageBehaviorTests`,
  `GuestCircuitHandlerTests` and client lifecycle tests first; existing browser
  module checks next; then the focused integrated simulator journey. Check
  applicable OUI/GUI requirements and existing CLI behavior. Use Aspire for
  application orchestration, not a custom launcher.
- **Documentation:** Update the diagram to show Api hosting ControlService only
  after cutover works. Record actual evidence, unresolved conformance gaps and
  task completion here; do not retain a second checklist in the design.

## Risks and blocked conformance work

- **Grant metadata:** Current DTOs lack the draft's grant sequence and explicit
  timestamp fields, although issuance-based expiry exists internally. The project
  owner must settle logical-versus-wire requirements before a separate versioned
  protocol change. This does not block compatible extraction; it blocks claiming
  complete grant-schema conformance. Do not add fields silently to closed schemas.
- **Join receipt:** The internal operation returns a private proof that is bound
  server-side; the hub returns an acknowledgement. Clarify that boundary in the
  design instead of exposing a secret merely to match ambiguous wording.
- **Existing assurance gaps:** Inspect logical-envelope versus full-frame size
  checks, real SignalR closed-schema rejection and expiry-triggered socket closure.
  Rejection tests and a substituted hub fixture are not proof of full host
  conformance. Keep necessary boundary checks with their owning task and identify
  behavior changes separately from extraction.
- **Cutover:** Never run two authority hosts against the same device. Restart
  discards guest authority; rollback also requires stopping the managed owner.
  Ask the operator to stop any existing process that blocks the work.

## Completion and deferred checks

Run focused checks alongside each change. Use xUnit v3/MTP v2,
AwesomeAssertions and FastMoq where useful, retaining direct value tests and
deterministic device/clock fakes. Prove behavior regressions fail before fixing
them. Documentation-only changes need content/link checks, not application runs.

Completion requires the target dependency boundaries, focused host/library/core
and consumer evidence, updated documentation, and explicit disposition of any
remaining contract gaps. Passing extraction checks alone is not full design
conformance. Report what actually ran and what remains blocked or deferred.

Full-suite or expensive validation requires approval; CI gates remain applicable
and must not be implied to have run locally. No new dependencies, sibling widget
edits, hardware qualification or physical operation are authorized by this plan.
This planning work ran no builds or tests.