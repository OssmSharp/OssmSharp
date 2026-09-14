# Repository instructions

## Stack

- .NET 10, C#, **ASP.NET Core Blazor Interactive Server + minimal APIs**.
- No MVC controllers, WebAssembly control host or browser Bluetooth.
- **Aspire AppHost + ServiceDefaults**; follow the
  [first-app guide](https://aspire.dev/get-started/first-app/?aspire-lang=csharp).
- `aspire run` builds/orchestrates services. No custom service launcher.
- Prefer native .NET/Aspire best practices; remove redundant boilerplate.
- All application CLIs use **`System.CommandLine`**.

## Structure and configuration

- **One class per file**; also separate records, interfaces, enums and test
  helpers. Match filenames to types. Exclude generated code; allow Razor partials.
- Reusable namespaces: `OSSMDotNet.*`. Web-specific: `OSSMDotNet.Web.*`.
- Register multi-consumer components through **`IServiceCollection` extensions**
  owned by the component. Centralize lifetimes; avoid speculative abstractions.
- Use **`Microsoft.Extensions.Options`** and typed DI configuration.
- Use **`System.ComponentModel.DataAnnotations`** attributes for constraints.
- Use **`IValidatableObject` / `IValidateOptions<T>`** for domain/cross-field checks.
- Require **`ValidateDataAnnotations()` + `ValidateOnStart()`**. Fail invalid
  startup; no parallel options parser or silent defaults.
- Git: LF in commits, CRLF on checkout via `.gitattributes`; no global overrides.

## Logging

- Use **`Microsoft.Extensions.Logging.ILogger<T>`** for operational diagnostics.
- Use Aspire ServiceDefaults/OpenTelemetry; no custom logging infrastructure.
- Use structured templates, appropriate levels and one owning failure log.
- No secrets, capability URLs, raw payloads or sensitive identifiers in logs.
- Reserve console output for intentional CLI results/help, not service logging.

## Authority and safety

- Keep GuestWeb and AdminService as **separate processes**.
- Only admin owns sessions, policy, timers, the single writer and device bridge.
- Guest exposes only Join/input/Stop/leave/scoped status; no operator/device access.
- Browser and Blazor state are untrusted; neither authorizes motion.
- One-box, accountless capabilities, one-click invitations; no accounts/database/cloud.
- Reuse `ossm-rs`'s `ossm-motion` through the admin-owned Rust adapter.
- Simulator only: no sibling edits, firmware flashing, hardware or physical qualification.
- Stop/lock override pending input. Only admin unlocks, into **paused**.
- Startup/reconnect never arms/resumes. Cancellation never proves device I/O stopped.
- Preserve bounded work, execution ownership and safety checks when simplifying.
- Read `.docs/specs/README.md` and affected device-control requirements; retain IDs.

## Memory and tests

- Use `Span`/`ReadOnlySpan` synchronously; `Memory`/`ReadOnlyMemory` for async I/O.
- Avoid needless copies, encoding round trips and hot-path allocations.
- Bound frames; clear pooled secrets; return buffers only after all consumers finish.
- No spans across `await`; prefer correctness over speculative optimization.
- **xUnit v3 + MTP v2**; no MSTest, xUnit v2 or VSTest.
- **FastMoq.Core** where useful; retain direct value tests and deterministic I/O/clock fakes using System.TimeProvider
- Tests before behavior; prove regressions fail before fixing.
- Run focused `dotnet test --project <project>` with `TESTINGPLATFORM_TELEMETRY_OPTOUT=1`.
- Never remove failing tests or claim unrun checks passed.
- Separate test projects into unit tests and integration tests, with shared test helpers. Integration test projects must be named with the assembly they're testing followed by ".IntegrationTests". Unit test projects the same, but ending with ".Tests".

## Boundaries

- Approved prototype dependencies only: explicit, pinned, necessary.
- Use compatible installed tools; record actual versions.
- Branches, commits, pushes, schemas and other-checkout changes need explicit approval.
- No secrets in source. Use Podman, not Docker, when containers are authorized.
