---
name: fastmoq-mtp2
description:
  Configure, run, and troubleshoot FastMoq tests with xUnit v3 and
  Microsoft.Testing.Platform v2 on .NET 10. Covers packages, filters,
  diagnostics, test results, and MTP-only compatibility.
---

# FastMoq, xUnit v3, and MTP v2

Follow the .NET 10/MTP v2 path in the
[xUnit guide](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform),
reviewed 2026-09-14. Repository policy comes from
[AGENTS.md](../../../AGENTS.md).

## Inspect first

Read `global.json`, `Directory.Build.props`, the test project, and nearby tests.
Keep approved SDK and package pins; tutorial versions do not authorize upgrades.
Run `dotnet --version` at the root to check SDK selection. For compatibility
questions, inspect resolved packages in `obj/project.assets.json`.

## Configure MTP v2

Use `xunit.v3.mtp-v2`, or `xunit.v3.core.mtp-v2` for core-only setups, not both.
Explicit MTP major selection starts with xUnit package `3.2.0`. Default `3.x`
packages use MTP v1; `4.0.0` onward uses MTP v2 and drops v1. The `xunit.v3`
name does not require a NuGet version starting with `3`.

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <IsTestProject>true</IsTestProject>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
</PropertyGroup>
```

Merge this into `global.json`, preserving `sdk`:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

The project property selects the executable runner; `global.json` selects
the SDK's `dotnet test` runner. Do not use `TestingPlatformDotnetTestSupport`.

xUnit v3 supports MTP directly. Do not add VSTest adapters,
`xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, MSTest, or xUnit v2.

## Write tests

Follow [fastmoq](../fastmoq/SKILL.md). Prefer `FastMoq.Core` and tracked,
provider-neutral collaborators. Use tracked Moq setups for delays and failures.
Check installed APIs rather than guessing. For regressions, first verify that
the test fails for the intended reason, then fix and rerun that scope.

## Run a focused scope

From the root:

```powershell
dotnet test --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj
```

Use `--project`, not a positional path. MTP options follow directly; no extra
`--` is needed. `dotnet run` still needs it to forward arguments.

| Need | Options |
| --- | --- |
| Class | `--filter-class "Namespace.Class"` |
| Method | `--filter-method "Namespace.Class.Method"` |
| Namespace | `--filter-namespace "Namespace"` |
| Trait | `--filter-trait "Category=Unit"` |
| Exclude | `--filter-not-class`, `--filter-not-method`, `--filter-not-namespace`, `--filter-not-trait` |
| Progress and diagnostics | `--xunit-info --xunit-diagnostics on` |
| Live output | `--show-live-output on` |
| TRX file | `--report-xunit-trx --report-xunit-trx-filename tests.trx` |

Replace example names with real tests. One filter accepts several values.
Do not substitute native xUnit flags such as `-class` or VSTest's `--filter`.
Verify tests actually ran; empty selections are not success. Preserve failures
and exit codes rather than changing runners.

## Troubleshoot

- Add `-?` to the test command to inspect installed options before changing
  packages.
- `testconfig.json` applies only to MTP. Follow the
  [xUnit schema](https://xunit.net/docs/testconfig-json-mtp).
- Test-result filename options take a basename. Set the folder with
  `--results-directory`; the default is `TestResults` under the output folder.
  Use explicit names to avoid generated user, machine, and timestamp values.
- Do not use VSTest's `--logger trx` or Coverlet collection commands.
  Microsoft's `--report-trx` needs its own extension and is not xUnit's option.
  For coverage, use the [MTP guide](https://xunit.net/docs/getting-started/v3/code-coverage-with-mtp).
- Test Explorer works in MTP-capable Visual Studio versions. Diagnose IDE
  compatibility instead of adding VSTest adapters or disabling MTP server support.

See [Microsoft's runner guidance](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test#mtp-mode-of-dotnet-test).
