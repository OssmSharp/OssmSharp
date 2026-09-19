# OSSM control specifications

## Scope

The system provides a reusable device/control core and a separately composed
remote-control application. The core covers the controller, device library,
device abstractions and shared models. Higher-order services and user interfaces
consume those contracts; they do not define the core's packaging or dependencies.

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

## Specification owners

Each behavior has one normative owner. Other documents link to that owner
rather than restate its rules. Existing requirement IDs remain stable; retained
CC IDs that overlap DC requirements are references, not parallel contracts.

| Specification | Owns | IDs |
| --- | --- | --- |
| [Reusable core](control-core.md) | Logical component boundaries, shared models, dependency direction and standalone verification | CC-01, CC-06, CC-08 through CC-11; PST-01 through PST-04 |
| [Device contracts and execution](device-control/spec.md) | Device API, stock/simulator implementation, controller lifecycle, single writer, Stop, limits, feedback, lockout and physical qualification | DC-01 through DC-09 |
| [Application, services and interfaces](application.md) | Session authority, grants, transport, deployment, operator/guest presentation and application conformance | CC-02 through CC-05, CC-07; DEP-01; SESSION-01/02; OUI-01 through OUI-06; GUI-01 through GUI-03 |

The [requirement terms](control-core.md#scope-and-ownership) define must, should,
may and can. The [repository instructions](../../AGENTS.md) govern development
and approvals; requirements do not authorize branches, commits, dependencies,
deployment or physical operation.

## Logical modules

The original module IDs remain navigation/ownership labels, not proposed
projects or a required implementation sequence.

| Module | Responsibility | Contract |
| --- | --- | --- |
| `device-control` | Reusable controller and device boundary | [Core](control-core.md) and [device execution](device-control/spec.md) |
| `session-relay` | Admission, service messages and ordered projections | [Invitations/sessions](application.md#invitations-and-sessions) and [transport](application.md#service-transport) |
| `host-authority` | Application approval, grants and policy integration | [Control authority](application.md#approval-and-control-authority) and [input freshness](application.md#guest-input-and-grant-freshness) |
| `guest-controls` | Operator/guest interaction and presentation | [User interfaces](application.md#user-interfaces) |
| `self-hosting` | Process composition, local bootstrap and service trust | [Deployment and trust](application.md#deployment-and-trust) |

Dependencies point inward: interfaces/hosts consume the service and core;
the controller consumes device abstractions and shared models; device
implementations consume their abstractions. The reusable core must not depend
on application services, transports, UI frameworks or the orchestrator.

The application's three-process runtime and one-box option are specified in
[application ownership](application.md#scope-and-ownership) and
[DEP-01](application.md#dep-01-local-one-box-operation). The existing
[architecture views](../diagrams/architecture.html) illustrate deployment and
enforcement; their current project labels are not normative packaging rules.

## Implementation references and exclusions

The [repository README](../../README.md) owns current build/run instructions.
Resolved SDK and package versions belong in `global.json`, project files and
central package configuration, not a second version inventory here. Core
tooling is specified in [core verification](control-core.md#implementation-and-verification);
application hosting/tooling belongs in [bootstrap](application.md#local-bootstrap-root-of-trust).

Stock protocol evidence and native simulator reuse are recorded in the
[device specification](device-control/spec.md). Software acceptance must not be
treated as [physical qualification](device-control/spec.md#physical-qualification).
Firmware replacement, multiple devices/guests, streaming trajectories,
unattended use, persistent motion profiles, media and automatic motion
resumption remain out of scope. Accounts, databases and cloud services are not
required for the approved local application.

The known rate-limit discrepancy remains recorded once under
[guest input freshness](application.md#guest-input-and-grant-freshness); this
reorganization does not resolve it by silently changing a limit.