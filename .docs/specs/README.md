# OssmSharp control specifications

## Scope

The system provides reusable device libraries, a standalone interactive CLI and
a separately composed web-control application. Device contracts, providers and
execution mechanics do not depend on web sessions, UI hosts or development
orchestration.

Retained requirement IDs describe logical responsibilities; the target project
and namespace names are fixed by the design and migration map. Renaming a
project must not change the meaning of its requirements. Public API names,
protocol fields, configuration keys and external dependency names remain
explicit where they are part of a technical contract.

The accepted MVP is one computer, one device, one local device user (the admin)
and one explicitly approved remote guest. Simulator-first implementation and
physical qualification gates remain. Earlier process-split decisions are
superseded by the direct-device design accepted on 25 September 2026; accepting
that design does not establish implementation or authorize hardware operation.

## Accepted runtime boundaries

The [web-control design](web-control-design.md#scope-and-ownership) owns the
accepted packaging and deployment decisions; the
[architecture diagram](../diagrams/architecture.html) depicts that target, not
completed implementation.

The [direct-device migration plan](../plans/direct-device-migration.md) owns the
implementation sequence, including repository instructions and the
OSSMDotNet-to-OssmSharp repository/project/namespace rename. M0 has aligned
the root instructions; the plan records subsequent implementation and evidence.
This design contract does not establish runtime migration acceptance.

- `Web.Api` is the device-owning web backend. It hosts the web application
	policy and selects one device provider through DI. `Web.AdminWeb` and
	`Web.GuestWeb` remain separate Blazor processes that call it over SignalR.
- `Cli` is an alternative direct device owner: an interactive prompt with a
	persistent connection, three motion inputs plus pattern, help, Stop and quit.
	It has no web controller, guest session, authentication or service-client
	dependency. Handled exit paths make a bounded, awaited best-effort Stop before
	disconnecting and releasing ownership.
- One machine-wide ownership lock excludes concurrent cooperating CLI/API
	owners. Detected external parameter changes latch an interruption; only the
	local admin can acknowledge it, into paused state. In the CLI this is an
	explicit local command, not a call to an admin website.
- `Device.Stock` and `Device.Simulator` implement the same device contract.
	Provider-specific packages are isolated by build/publish profiles. Neither
	the CLI nor the simulator adapter starts, owns or terminates the simulator.
- `Defaults` configures shared host-neutral logging and telemetry;
	`Web.ServiceDefaults` adds web-only defaults. Neither requires Aspire, its
	dashboard or a running collector. Device primitives depend on neither.
- AppHost is optional local development/test orchestration with convenient
	simulated/actual arrangements. It is not a production dependency or a device
	writer.

This deliberately couples web API and web device-connection lifetimes for MVP
simplicity. It does not claim an independently deployable device service. The
standalone ControlService/ControlClient topology is superseded, as is the
adapter-owned simulator child-process arrangement. Existing requirement IDs
remain; the owning documents distinguish web policy from direct CLI mechanics.

Migration acceptance must exercise the CLI with every web executable and
AppHost absent, and the API without either UI host or AppHost. Verify configured
logging/telemetry, provider dependency isolation, cross-process exclusion,
latched interruption, shutdown Stop attempts and independent simulator lifetime.
Documentation changes and compilation do not establish these outcomes.

## Specification owners

Each behavior has one normative owner. Other documents link to that owner rather
than restate its rules. Existing requirement IDs remain stable; retained CC IDs
that overlap DC requirements are references, not parallel contracts.

| Specification                                            | Owns                                                                                                                                        | IDs                                                                                             |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| [Web-control design](web-control-design.md) | Accepted architecture, project mapping, CLI composition, web authority, transport, deployment and verification | CC-01 through CC-11; PST-01 through PST-04; DEP-01; SESSION-01/02; OUI-01 through OUI-06; GUI-01 through GUI-03 |
| [Device contracts and execution](device-control/spec.md) | Device API, stock/simulator implementation, controller lifecycle, single writer, Stop, limits, feedback, lockout and physical qualification | DC-01 through DC-09                                                                             |

The [requirement terms](web-control-design.md#scope-and-ownership) define must,
should, may and can. The [repository instructions](../../AGENTS.md) govern
development and approvals; requirements do not authorize branches, commits,
dependencies, deployment or physical operation.

The [device adapter design](device-control/adapters.md) records Shiny BLE,
native simulation, DI selection, selected enrollment/configuration and
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

The runtime boundaries are specified above and illustrated in the
[architecture diagram](../diagrams/architecture.html). The three web processes
and alternative CLI owner are intentional; DEP-01 remains the one-computer
operation requirement. Device libraries remain reusable without web hosts.

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

The [earlier implementation plan](../plans/web-control-implementation.md)
retains historical host/library-extraction evidence, not acceptance of this
migration. The [resolved decisions](web-control-design.md#resolved-direction-and-review)
record current decisions and compatibility constraints. The migration plan
records implementation status; no physical operation is authorized.
