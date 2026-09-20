# Plan: Shared validation and ControlService organization

Date: 20 September 2026

Status: Planned, not implemented by this task. Saved from the planning
discussion and source investigation on 19 September 2026. Recheck source
locations and existing work before implementation; the repository may have
changed since that investigation. Paths below are relative to the repository
root.

## Outcomes

1. Define validation centrally and reuse it. Align all rules for the same kind
   of input, rather than merely extracting existing divergent checks.
2. Remove `Prototype` from project-owned naming throughout the repository.
3. Organize ControlService into shallow, namespace-aligned subcomponent folders.

## Decisions and boundaries

- Limits accept integers from 0 to 100. Either zero limit prevents arming.
- Internal stopped tuples may contain zero stroke/depth when speed is zero.
- Guest Send requires positive stroke/depth. Apply that contract at every
  guest-submit entry point, including direct SignalR and Core calls, not just
  GuestWeb.
- Rename the device ownership lock directly, without a legacy compatibility
  alias. Old and new lock names do not coordinate. Do not overlap old and new
  service instances during runtime validation or stop running processes without
  permission.
- Retain distinct guest/device models and their wire shapes. Centralization does
  not require merging `ControlValues` with `DesiredState`.
- Preserve authorization, freshness, execution ownership, atomic limit-conflict
  rejection, Stop precedence and execution-time revalidation.

### Validation technology

Recommend native `System.ComponentModel.DataAnnotations`, `IValidatableObject`
and small typed shared rules. This follows repository conventions without adding
a dependency. Use `IValidateOptions<T>` where appropriate for options, retaining
`ValidateDataAnnotations()` and `ValidateOnStart()`.

FluentValidation remains an alternative, not an approved dependency. Its core
library supports .NET 10 and uses Apache 2.0. The unsupported package is
`FluentValidation.AspNetCore`, which supplies the legacy MVC integration.
FluentValidation does not provide built-in Blazor integration.

Awesome Assertions is a community fork of Fluent Assertions for test assertions,
not application input validation. Its discovery does not change this plan's
validation recommendation. The requested filename does not imply adoption of
FluentValidation.

## Findings from the planning investigation

- `PrototypeEngine.ValidDesired` combines intrinsic ranges/profile/geometry with
  active limits. `SetLimits` and `PolicyAllowsArming` contain related rules.
- `Join.SubmitRun` repeats tuple validation and requires positive stroke/depth.
  `GuestControlOperations.IsValidInput` checks only envelope shape, so direct
  guest submissions bypass the UI-only positivity check.
- The operator maximum-depth slider and error message require at least 1, while
  Core accepts 0. GuestWeb and `guest-session.js` clamp the displayed maximum to
  at least 1, hiding zero policy values.
- Guest-origin options and invitation creation disagree on raw URI spelling and
  HTTPS loopback. Control-client options and the CLI separately implement
  control-origin validation.
- Service-key options repeat a canonical 43-character base64url expression,
  while runtime capability matching accepts a broader syntax before comparing
  digests.
- ControlService implementation files are mostly at the project root. Friend
  assembly access is assembly-based and survives namespace moves.
- No dedicated Models test project was found. Existing consumer tests provide
  suitable boundaries; do not create a test project solely for this refactor.

Requirements remain owned by [the capability map](../specs/README.md),
[ControlCore contract](../specs/control-core.md) and
[device-control specification](../specs/device-control/spec.md). Retain
`SESSION-01`, `CC-06`, `CC-07`, `CC-09`, `DC-03`, `DC-04`, `DC-05`, `DC-07` and
`DC-09` when updating affected requirements.

## Shared rule ownership

Use small domain-specific helpers under `src/OSSMDotNet.Models/Validation`, with
the matching namespace. Retain `ControlProtocol` for protocol rules. Do not add
a universal validation framework, registry or speculative interfaces.

### Control values and limits

Define percentage bounds, supported pattern 0, fixed sensation 50, geometry,
zero/nonzero requirements and policy eligibility once. Provide distinct named
operations for:

- Intrinsic tuple validity: fields in range, supported profile values, stroke
  not greater than depth, and positive stroke for nonzero speed.
- Tuple validity within supplied limits.
- Guest-run validity, including positive stroke/depth even at zero speed.
- Valid limits and positive limits required for arming.

Adapters for `ControlValues`/`DesiredState` and `ControlLimits`/`GuestLimits`
delegate to those definitions. Use shared constants in scalar annotations and
shared cross-field rules in `IValidatableObject`. Positional-record annotations
must target the properties correctly.

Repeated execution checks call the same typed rules directly, without
reflection, error-list construction or adapter-record allocation on each tick.
Do not create a second complete handwritten validator alongside annotation
validation. Verify that the adapters accept and reject the same inputs.

### Protocol fields

Centralize named bounds and predicates for:

- Correlation, session, epoch and grant identifiers: nonblank, at most 64
  characters for their respective transport fields.
- Invitation proof: transport bound of 128 characters, separate from proof
  authentication.
- Input version 2, positive policy version and positive safe input sequence up
  to 9,007,199,254,740,991.

Keep internal connection identifiers and creation scopes distinct where their
contracts differ. Syntax validation does not authenticate a proof, establish
freshness or enforce a strictly increasing sequence; those checks remain with
the authoritative state owner.

### Origins

Share parsing and raw-spelling checks, with distinct named entry points for
advertised guest origins and control-service origins.

- Guest origins follow `SESSION-01`: absolute origin, no credentials, non-root
  path, query or fragment; reject invalid raw spelling, whitespace and
  backslashes. HTTP requires literal `localhost`, `127.0.0.1` or `[::1]`. Align
  HTTPS loopback acceptance between options and invitation generation.
- Align the CLI and control-client options under their existing control-origin
  policy: loopback HTTP or non-loopback HTTPS. Do not silently broaden access.
- Preserve accepted optional root slashes and effective default ports unless
  authoritative requirements say otherwise. `Uri.Port` does not require a port
  to be explicitly written; do not invent that requirement.
- Keep port collisions, bind scope, certificates and listener restrictions in
  their owning options/hosting components.
- Keep AdminWeb's exact request Host/Origin checks separate and unchanged.

### Service keys and configuration

Reuse one canonical 256-bit base64url service-key format in option constraints
and runtime service authentication. Preserve constant-time digest comparisons,
buffer clearing and secret redaction. Do not apply service-key shape rules to
shorter epoch/grant identifiers or conflate service keys with guest authority.

Share repeated listener-port and SignalR keepalive/timeout constraints,
including timeout being at least twice keepalive. Keep command timeouts and
listener-specific security policies explicit. Do not introduce a second options
parser or fallback configuration.

### Browser and feedback boundaries

Render numeric input constraints from shared .NET definitions and actual status.
Delegate complex browser acceptance through the existing managed `SubmitRun`
path, with independent enforcement in Core. Browser draft adjustment is a UI
convenience, not permission to normalize server commands silently.

Device feedback is observed state, not guest Send. Do not apply guest positivity
or active guest caps to feedback. Reuse intrinsic rules only where the feedback
contract is equivalent. Enforce required finite readings and known shape at
sample intake under `DC-07`, without inventing physical telemetry ranges.
Preserve external-change lockout classification and precedence; validation must
not downgrade a latched lock to a recoverable fault or prevent Stop.

## Implementation sequence

### 1. Record contracts and reproduce inconsistent behavior

- Update existing specifications before changing durable behavior. Record the
  shared-input contract, zero-limit semantics and distinction between guest Send
  and internal stopped states. Retain requirement IDs.
- Leave the documented 10-per-second versus 20-capacity rate-limit discrepancy
  unchanged. It is a separate stateful policy decision.
- Establish focused baseline checks. Add regression tests for each verified
  mismatch and observe the intended failure before changing behavior.
- Verify rejected inputs do not change accepted sequence, desired state, lease
  or bridge writes, subject to existing authentication, expiry and
  attempt-counting semantics. Do not manufacture failing tests for mechanical
  renames.

### 2. Rename and organize

- Rename `PrototypeEngine` to `ControlEngine`, including its constructor, DI,
  consumers, test classes, filenames and helper references.
- Rename `tests/browser/prototype_smoke.py` to `control_smoke.py` and
  `.docs/specs/prototype.md` to `simulator-first.md`; update references.
- Replace project-owned prototype branding in messages, documentation,
  repository instructions, AppHost and the simulator README. Preserve all
  simulator-only and unqualified-hardware caveats.
- Replace stale `.prototype` artifact naming with neutral local-artifact naming,
  such as `.ossm-local`, without moving or deleting unrelated ignored output.
- Rename the semaphore to `ossm-web-controls-device-ownership` and update its
  exact-name test. Do not add a compatibility alias.
- Leave JavaScript `Node.prototype` and third-party/generated/package content
  unchanged.
- Move service files according to the map below. Update namespaces, imports,
  `Program`, DI and tests together, preserving behavior and lifetimes.
- Perform renaming and folder moves sequentially because they share files. Build
  affected projects and run focused Core, registration, authorization and
  ownership-lock checks before behavioral work.

### 3. Implement shared rules and migrate consumers

- Implement Models rules and native annotation adapters after the relevant
  regression tests. Preserve wire fields, protocol version, transport roles and
  `/hubs/control/v1`. Add XML documentation to new public domain APIs.
- Migrate Core limit checks, arming eligibility, guest submission, tuple checks,
  limit-conflict checks, motion authorization and final non-Stop write checks.
  Keep authentication before state-changing expiry, existing error precedence,
  authenticated-attempt accounting and final validation under execution
  ownership.
- Apply guest-run positivity at submission. Internal zero/Stop handling uses
  intrinsic rules; guest-run validation must never block Stop.
- Preserve revalidation after asynchronous boundaries and immediately before
  writes. Keep conflict checks for accepted, observed, pending, in-flight and
  completed-but-unconfirmed work.
- Migrate service envelope/correlation checks and configuration consumers:
  `ControlServiceOptions`, `ControlClientOptions`, CLI parsing, scoped
  service-key validation and shared timing/port constraints.
- Align operator and guest UI consumers. Permit zero maximum depth in the
  operator slider and describe both limits as 0–100. Reuse the shared arming
  rule alongside existing readiness checks. Use shared guest-run validation in
  `Join.SubmitRun`.
- Preserve actual zero limits in guest status and JavaScript. At zero depth,
  explain that limits disable control and disable Send. Preserve unsent drafts;
  avoid invalid slider minimum/maximum combinations or presenting 1 as the
  actual limit. Restoring positive limits must not arm, resume or dispatch
  unsent values. Retain Stop and connection-loss behavior.
- Do not add a JavaScript validation engine or modify copied RetroWebUi assets.
- Apply feedback-format requirements at sample intake with tests for fault,
  lockout and Stop precedence. Do this after other Core changes to avoid
  concurrent edits to the engine.

The shared rules are a prerequisite for consumer migration. Once their contracts
are settled, UI and configuration changes can proceed independently of service
changes on separate files.

### 4. Verify and close documentation

- Run focused affected xUnit v3/MTP v2 tests with nonzero discovery. Use
  existing FastMoq patterns where useful and deterministic device/clock fakes.
- Include actual SignalR boundary tests that bypass rendered controls.
- Run the fastest applicable existing browser check for zero limits, disabled
  Send/Allow control, truthful display, positive-limit restoration without
  rearming or unsent dispatch, valid sending, Stop and feedback. Use the
  existing Aspire and uv-managed Playwright workflow, without Node or a custom
  launcher.
- Update `.docs/diagrams/architecture.html` descriptions for internal
  organization and shared validation ownership, retaining project/process
  boundaries and avoiding a diagram redesign.
- Update existing README, specification and browser-script references.
- Check diagnostics, file/namespace consistency, links and remaining
  case-insensitive `prototype` names and paths. Remaining matches must be
  language constructs or excluded external/generated content.
- Review the diff for changed permissions, exposed secrets, wire-contract drift,
  duplicated rules and unintended behavior. Report unrun checks; full-suite or
  expensive local validation needs separate approval.

## ControlService folder map

Paths are relative to `src/OSSMDotNet.ControlService`. Namespaces follow
folders.

| Folder       | Files                                                                                                                                                                                                                                           |
| ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Authority/` | `ControlAuthority.cs`, `ControlOperations.cs`, `GuestControlOperations.cs`                                                                                                                                                                      |
| `Hub/`       | `ControlHub.cs`, `ControlHubEndpointRouteBuilderExtensions.cs`, `ControlHubConnectionRegistry.cs`, `ControlHubPayload.cs`, `IControlHubPushSink.cs`, `SignalRControlHubPushSink.cs`, `ControlPushDispatcher.cs`                                 |
| `Security/`  | `ServiceKeyAuthenticationDefaults.cs`, `ServiceKeyAuthenticationHandler.cs`, `ServiceKeyAuthenticator.cs`, `ControlServiceRoles.cs`, `ControlServicePolicies.cs`                                                                                |
| `Device/`    | `DeviceLoop.cs`, `DeviceOwnershipLock.cs`                                                                                                                                                                                                       |
| `Activity/`  | `ControlActivityProvider.cs`, `ControlActivityWriter.cs`, `ControlServiceLog.cs`                                                                                                                                                                |
| `Hosting/`   | `ServiceCollectionExtensions.cs`, `ControlServiceBoundary.cs`, `ControlHostFilteringOptionsConfiguration.cs`, `SignalRHubOptionsConfiguration.cs`, `EnvironmentCertificatePasswordProvider.cs`, `IControlServiceCertificatePasswordProvider.cs` |
| `Models/`    | `ControlServiceOptions.cs`, `ServiceKeyAuthenticationOptions.cs`, `ServiceIdentity.cs`, `HubGuestSession.cs`                                                                                                                                    |

Keep `Program.cs`, `ControlService.csproj`, `appsettings.json` and
`Properties/AssemblyInfo.cs` in their existing locations. Retain one composition
entry point rather than adding registration machinery per folder. Tests may
retain their current folders/namespaces with updated imports; friend assembly
names do not change.

## Principal change locations

- `src/OSSMDotNet.Models/`: `ControlValues`, `DesiredState`, `ControlLimits`,
  `GuestLimits`, `GuestInput`, `MotionInput`, `ControlProtocol` and shared
  rules.
- `src/OSSMDotNet.ControlCore/PrototypeEngine.cs`: rename, tuple/policy checks,
  `ValidateOrigin`, `Submit`, `OnSample` and dispatch safeguards.
- `src/OSSMDotNet.ControlService/`: the folder map above, especially guest
  operations, options, authentication, DI and device ownership.
- `src/OSSMDotNet.ControlClient/ControlClientOptions.cs` and
  `src/OSSMDotNet.Cli/ControlCommand.cs`: equivalent control-origin validation.
- `src/OSSMDotNet.ServiceDefaults/CapabilityTokens.cs`, `AdminOptions.cs` and
  `GuestOptions.cs`: scoped format and configuration-rule reuse.
- `src/OSSMDotNet.AdminWeb/Components/Pages/Home.razor` and its code-behind:
  limits controls, messages and `CanAllow`.
- `src/OSSMDotNet.GuestWeb/Components/Pages/Join.razor`, its code-behind and
  `src/OSSMDotNet.GuestWeb/wwwroot/guest-session.js`: submission and zero-limit
  UI.
- Existing specifications, architecture diagram and browser smoke documentation.

## Acceptance evidence

Use shared test vectors across consumers where they enforce the same contract.

| Area                   | Required cases and outcomes                                                                                                                                                                                                                                                                |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Guest run              | Inclusive boundaries, negatives, 101, unsupported pattern/sensation, stroke greater than depth, policy excess; zero speed with positive geometry accepted; zero stroke/depth rejected in GuestWeb, service, direct SignalR and Core.                                                       |
| Internal stopped state | Valid zero-valued stopped tuples remain supported; Stop is not blocked by guest-run rules.                                                                                                                                                                                                 |
| Limits                 | `(0,0)`, `(0,100)`, `(100,0)`, `(100,100)`, invalid -1/101, unchanged no-op and atomic conflict rejection. Positive limits alone never rearm or renew a lease.                                                                                                                             |
| Origins                | Literal localhost/IPv4/IPv6, HTTPS remote, effective default/explicit/invalid ports, optional root slash, credentials, query/fragment, paths/dot paths, whitespace, backslashes and noncanonical loopback spellings. Same-kind consumers agree; different security policies stay distinct. |
| Protocol               | Null/empty/whitespace, exact maximum and maximum-plus-one text; sequence 0/1/maximum/maximum-plus-one, duplicate and out-of-order submissions under authenticated state.                                                                                                                   |
| Service keys           | Canonical and malformed formats agree across options/runtime; wrong keys and wrong roles remain denied; no secret logging.                                                                                                                                                                 |
| Feedback               | Malformed/nonfinite required readings cannot establish readiness or authorize ordinary writes; external lock remains latched and Stop remains available.                                                                                                                                   |
| Structure              | DI lifetimes, authorization, endpoint contracts, push delivery, disposal and single device ownership remain unchanged.                                                                                                                                                                     |

Extend existing engine/boundary tests, `LimitsPolicyTests`,
`GuestControlOperationsTests`, `ControlProtocolContractTests`,
`ControlHubContractTests`, options/boundary tests,
`ControlHubConnectionFactoryTests`, `ControlCommandTests`, service-key tests,
`DeviceOwnershipLockTests`, `JoinPageBehaviorTests` and affected AdminWeb tests.
Update engine test names with the rename. Do not add test dependencies or a new
test project by default.

## Exclusions

No hardware operation or qualification, firmware changes, sibling-repository
edits, new dependencies or schemas, transport/authentication redesign, DTO
merging, rate-limiter redesign, cryptographic replacement, speculative physical
telemetry bounds or startup/reconnect rearming. No branches, commits, pushes or
history rewriting. Saving this plan does not perform implementation or
validation.

## Library references

- [FluentValidation documentation](https://docs.fluentvalidation.net/en/latest/)
  and
  [Apache-2.0 license](https://raw.githubusercontent.com/FluentValidation/FluentValidation/main/License.txt).
- [ASP.NET integration guidance](https://docs.fluentvalidation.net/en/latest/aspnet.html)
  and
  [unsupported integration package](https://github.com/FluentValidation/FluentValidation.AspNetCore).
- [Blazor integration](https://docs.fluentvalidation.net/en/latest/blazor.html).
- [Awesome Assertions](https://github.com/AwesomeAssertions/AwesomeAssertions):
  the community test-assertion fork, not an input-validation replacement.
