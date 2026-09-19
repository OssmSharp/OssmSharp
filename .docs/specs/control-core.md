# Reusable control core

## Scope and ownership

The core consists of the controller, device implementations, device abstractions
and shared models. These are logical components, not required project, assembly,
namespace or folder names. Refactoring their packaging must not change their
contracts or dependency direction.

The core must be usable without a web host, browser, remote guest session,
command-line interface or application orchestrator. A consuming application must
compose one execution owner per device and supply trusted authorization
decisions. The core must enforce those decisions at execution; it must not
implement presentation or infer authority from a UI state.

**Must** denotes an obligation, **should** a recommendation, **may** permission
and **can** capability or possibility. **Must not** denotes prohibition;
**cannot** describes inability. Firmware facts remain descriptive.

| Logical component      | Responsibility                                                                                       | Permitted internal dependencies                                                          |
| ---------------------- | ---------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| Device abstractions    | Device interface, motion settings, observations, states and pattern identifiers                      | No other core or application component                                                   |
| Device implementations | Stock protocol mapping, native connection ownership and simulator process integration                | Device abstractions                                                                      |
| Shared models          | Immutable controller/application data contracts, explicit units, outcomes and versioned message data | Device abstractions only where a core value is required; no implementation or host types |
| Controller             | Lifecycle, limits, trusted execution guards, scheduling, single writer, Stop and lockout             | Device abstractions and shared models, never a concrete device implementation            |

Framework libraries for hosting, options, logging and timing may support core
lifetime management; ASP.NET Core, Blazor, SignalR, Aspire and UI components
must remain outside the core. The application composition root may reference
concrete device implementations to select an adapter. That selection must not
become a dependency of the controller or shared models.

## Requirement ownership

The [device-control specification](device-control/spec.md) owns DC-01 through
DC-09, the shared device contract and both adapter implementation contracts.
This document owns core composition and maps the retained CC identifiers to
those execution rules. The [application specification](application.md) owns
remote admission, grants, service transport, hosting and presentation. A
cross-reference applies the owning rule; it does not define a second version.

| ID    | Core obligation or authoritative reference                                                                                                                                                                                                                                                                                                                                           |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| CC-01 | The controller must start disarmed at zero and satisfy [DC-01](device-control/spec.md#dc-01-explicit-lifecycle-and-readiness) before accepting motion. Arming must require trusted owner authorization and valid limits; remote guest approval is an additional [application prerequisite](application.md#approval-and-control-authority), not a device-library dependency.          |
| CC-06 | Policy changes must satisfy [DC-03](device-control/spec.md#dc-03-capability-and-value-contract). Application policy-version/grant publication is specified in [Live operator limits](application.md#live-operator-limits).                                                                                                                                                           |
| CC-08 | The controller must expose immutable snapshots through an ordered initial/live observation path. Snapshots must retain the observation semantics in [DC-07](device-control/spec.md#dc-07-feedback-loss-and-disposal), including pending I/O and unconfirmed outcomes, without exposing native handles or application secrets. Wire projection and display belong to the application. |
| CC-09 | Scheduling and Stop must satisfy [DC-04](device-control/spec.md#dc-04-one-writer-and-bounded-pending-work) and [DC-05](device-control/spec.md#dc-05-stop-wins-without-concurrent-writes), including the authority-revision guard in [DC-02](device-control/spec.md#dc-02-narrow-consumer-surface).                                                                                   |
| CC-10 | Unexpected-change detection and trusted-owner recovery must satisfy [DC-09](device-control/spec.md#dc-09-external-parameter-changes-latch-an-admin-only-lockout). Applications must not offer less restrictive recovery paths.                                                                                                                                                       |
| CC-11 | Faults, logical timeouts and disposal must satisfy [DC-07](device-control/spec.md#dc-07-feedback-loss-and-disposal) without releasing unsettled I/O or reviving motion.                                                                                                                                                                                                              |

CC-02 through CC-05 and CC-07 retain their IDs in the application specification;
they describe remote-session behavior, not prerequisites for standalone core
use.

## Component structure

PST identifiers are retained. Naming-specific migration rules are replaced by
logical ownership rules so future project renames do not require a contract
edit.

| ID     | Requirement                                                                                                                                                                                                                             | Conformance evidence                                                                                                                                        |
| ------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PST-01 | Source and test organization must match their chosen namespaces and logical ownership. Requirements must not prescribe current project names. External package namespaces must remain unchanged by local refactoring.                   | Renaming a local assembly or folder leaves behavior and dependency boundaries unchanged.                                                                    |
| PST-02 | Application host defaults must own shared host configuration, local HTTP protection and capability-token helpers. Core components must not depend on that host support component.                                                       | Core builds and tests without loading host configuration or application security services.                                                                  |
| PST-03 | Shared models must own pure data contracts rather than service implementations. Command-line clients and orchestration must remain separate application components, not responsibilities of the models or controller.                   | Models compile without hosting, transport or UI references; clients do not become alternate device owners.                                                  |
| PST-04 | Device implementations and their abstractions must be independently buildable and consumable. Abstractions must own `IOssmDevice`, `DeviceState`, `MotionSettings` and device enums; implementations must own private protocol records. | Device components have no dependency on the controller, application models, service or UIs. Direct adapter integration tests run without application hosts. |

Public domain types must have valid XML documentation, with one type per file.
Shared models must remain immutable data, not shared mutable authority. They
must use explicit units and result/state variants rather than ambiguous success
flags. Versioned wire data may be represented as plain models, but must not
expose native handles, trusted in-process guards or implementation objects. The
application owns wire versions and security semantics.

## Implementation and verification

The core must use .NET 10, nullable-aware C#, asynchronous disposal and typed,
startup-validated options where configuration is needed. Timing must use
`TimeProvider` elapsed-time APIs; wall-clock changes must not renew authority or
observation freshness. Operational diagnostics must use `ILogger<T>` with
structured, bounded, non-sensitive fields, not a custom logging framework.

Stock and simulator implementations must satisfy the same observable device
contract. The
[stock implementation](device-control/spec.md#stock-firmware-implementation)
owns BLE-specific behavior; the
[simulator implementation](device-control/spec.md#simulator-implementation) uses
direct native-engine state. Consumers must not contain simulation branches.

Core tests must exercise the controller against deterministic device and clock
fakes. Device integration tests must exercise the adapter directly without
starting application hosts. Tests must cover observable ordering, outcomes,
bounded work and resource ownership, not private class layouts. .NET tests must
use xUnit v3/MTP v2 and FastMoq where useful; native engine tests may use their
native runner. No browser or physical device is required for core conformance.

Software conformance cannot establish hardware safety. Physical use remains
subject to the
[qualification gates](device-control/spec.md#physical-qualification).
