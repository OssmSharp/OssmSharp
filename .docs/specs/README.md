# OSSM control specifications

## Scope

The system provides a reusable device/control core and a separately composed
remote-control application. The core covers the controller, device library,
device abstractions and shared models. Higher-order services and user interfaces
consume those contracts; they do not define the core's packaging or
dependencies.

Component names in these specifications are logical roles, not fixed project,
assembly or namespace names. A project rename must not change requirement
meaning. Public API names, protocol fields, configuration keys and external
dependency names remain explicit where they are part of a technical contract.

The approved scope is simulator-first, with one device and one explicitly
approved remote guest. Physical use remains subject to qualification. The
simulator-first direction/dependencies were approved on 14 September 2026, and
the separate service/SignalR direction on 16 September 2026. These approvals do
not establish completed implementation, hardware safety or authorization to
operate a device.

The replacement [web-control design](web-control-design.md#scope-and-ownership)
is DRAFT. It proposes `OSSMDotNet.ApiService` as the executable SignalR host and
`OSSMDotNet.ControlService` as a business logic library. The historical approvals
above do not approve this split or the replacement design as a whole, and the
draft does not establish implementation.

## Specification owners

Each behavior has one normative owner. Other documents link to that owner rather
than restate its rules. Existing requirement IDs remain stable; retained CC IDs
that overlap DC requirements are references, not parallel contracts.

| Specification                                            | Owns                                                                                                                                        | IDs                                                                                             |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| [Web-control design (DRAFT)](web-control-design.md)       | Core boundaries, shared models, dependency direction, session authority, grants, transport, deployment, interfaces and conformance          | CC-01 through CC-11; PST-01 through PST-04; DEP-01; SESSION-01/02; OUI-01 through OUI-06; GUI-01 through GUI-03 |
| [Device contracts and execution](device-control/spec.md) | Device API, stock/simulator implementation, controller lifecycle, single writer, Stop, limits, feedback, lockout and physical qualification | DC-01 through DC-09                                                                             |

The [requirement terms](web-control-design.md#scope-and-ownership) define must,
should, may and can. The [repository instructions](../../AGENTS.md) govern
development and approvals; requirements do not authorize branches, commits,
dependencies, deployment or physical operation.

The [device adapter design](device-control/adapters.md) records Shiny BLE,
native simulation, DI selection, proposed enrollment/configuration and
integration gates. It supports the device specification without adding another
owner for DC-01 through DC-09.

## Logical modules

The original module IDs remain navigation/ownership labels, not proposed
projects or a required implementation sequence.

| Module           | Responsibility                                         | Contract                                                                                                                                 |
| ---------------- | ------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `device-control` | Reusable controller and device boundary                | [Core](web-control-design.md#reusable-core-boundary) and [device execution](device-control/spec.md)                                       |
| `session-relay`  | Admission, service messages and ordered projections    | [Invitations/sessions](web-control-design.md#invitations-and-sessions) and [transport](web-control-design.md#service-transport)            |
| `host-authority` | Application approval, grants and policy integration    | [Control authority](web-control-design.md#approval-and-control-authority) and [input freshness](web-control-design.md#guest-input-and-grant-freshness) |
| `guest-controls` | Operator/guest interaction and presentation            | [User interfaces](web-control-design.md#user-interfaces)                                                                                  |
| `self-hosting`   | Process composition, local bootstrap and service trust | [Deployment and trust](web-control-design.md#deployment-and-trust)                                                                        |

Dependencies point inward: interfaces/hosts consume the service and core; the
controller consumes device abstractions and shared models; device
implementations consume their abstractions. The reusable core must not depend on
application services, transports, UI frameworks or the orchestrator.

The draft application's three-process packaging and one-box option are specified
in [application ownership](web-control-design.md#scope-and-ownership) and
[DEP-01](web-control-design.md#dep-01-local-one-box-operation). The existing
[architecture views](../diagrams/architecture.html) illustrate deployment and
enforcement; their current project labels are not normative packaging rules.

## Implementation references and exclusions

The [repository README](../../README.md) owns current build/run instructions.
Resolved SDK and package versions belong in `global.json`, project files and
central package configuration, not a second version inventory here. Core tooling
is specified in
[core verification](web-control-design.md#implementation-and-verification);
application hosting/tooling belongs in
[bootstrap](web-control-design.md#local-bootstrap-root-of-trust).

Stock protocol evidence and native simulator reuse are recorded in the
[device specification](device-control/spec.md). Software acceptance must not be
treated as
[physical qualification](device-control/spec.md#physical-qualification).
Firmware replacement, multiple devices/guests, streaming trajectories,
unattended use, persistent motion profiles, media and automatic motion
resumption remain out of scope. Accounts, databases and cloud services are not
required for the approved local application.

The [implementation plan](../plans/web-control-implementation.md) owns the task
list and acceptance evidence for the host/library split. The
[resolved decisions](web-control-design.md#resolved-direction-and-review) record
the selected rolling rate limit and coordinated public-type migration; neither
decision establishes completed implementation.
