---
name: fastmoq-mtp2
description:
  Use when writing, configuring, running, or troubleshooting repository tests with FastMoq, xUnit
  v3, and Microsoft.Testing.Platform v2 on .NET 10. Covers package selection, runner setup, focused
  filters, diagnostics, reports, and MTP-only compatibility.
---

# FastMoq, xUnit v3, and MTP v2

Distilled from
[xUnit.net's Microsoft Testing Platform guide](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform),
reviewed 2026-09-14. Use its .NET 10/MTP v2 path, not its older SDK examples. FastMoq policy comes
from [AGENTS.md](../../../AGENTS.md), not the xUnit guide.

## Inspect the existing setup

Read `global.json`, `Directory.Build.props`, the target test project, and nearby tests. Preserve
approved SDK and package pins; tutorial sample versions are not upgrade recommendations. Check
`dotnet --version` from the repository root when diagnosing SDK selection: the target framework
alone does not select the SDK. Inspect resolved packages in `obj/project.assets.json` when
compatibility is unclear. This skill does not authorize dependency additions or upgrades.

## Configure MTP v2

Use `xunit.v3.mtp-v2`, or `xunit.v3.core.mtp-v2` for an existing core-only setup, in place of the
corresponding default xUnit package. Do not reference both alternatives. Explicit MTP major-version
selection starts at xUnit v3 package version `3.2.0`: default `3.x` packages use MTP v1, while
`4.0.0` onward defaults to MTP v2 and removes MTP v1 support. The `xunit.v3` name does not imply
that its NuGet version must start with `3`.

Keep test projects executable and select the MTP command-line runner:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <IsTestProject>true</IsTestProject>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
</PropertyGroup>
```

Select MTP for `dotnet test` in the root `global.json`. Merge this fragment; preserve the existing
`sdk` section:

```json
{
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

These settings have separate jobs: `UseMicrosoftTestingPlatformRunner` selects the executable's
command-line experience; `global.json` selects the SDK's `dotnet test` runner. Do not use the legacy
`TestingPlatformDotnetTestSupport` property for this .NET 10 setup.

xUnit v3 includes native MTP support. Do not add `xunit.runner.visualstudio`,
`Microsoft.NET.Test.Sdk`, MSTest, or xUnit v2. The upstream recommendation to retain VSTest adapters
for older development environments does not apply to this repository's MTP-only contract.

## Write tests with the right collaborators

- Prefer `FastMoq.Core` over the aggregate package when extra integrations are unnecessary. Use
  provider-neutral APIs for dependency-aware construction and tracked collaborators; follow existing
  tests and the installed package's documentation rather than guessing APIs.
- Configure tracked Moq mocks with setups for delayed responses and device failures.
- Write behavioral tests before implementation. For regressions, prove the test fails for the
  intended reason, then fix the behavior and rerun that scope.

## Run the smallest useful scope

Run from the repository root in PowerShell, replacing the project as needed:

```powershell
dotnet test --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj
```

Use `--project`, not a positional project path. Add MTP/xUnit options directly to `dotnet test`; an
extra `--` is not required in .NET 10 MTP mode, though it can explicitly delimit forwarded
arguments. `dotnet run` still uses `--` to forward arguments, for example:
`dotnet run --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj -- -?`.

Replace the illustrative names below with real test names:

| Need                                | MTP/xUnit options                                                                                                  |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| Select a class                      | `--filter-class "Namespace.Class"`                                                                                 |
| Select a method                     | `--filter-method "Namespace.Class.Method"`                                                                         |
| Select a namespace                  | `--filter-namespace "Namespace"`                                                                                   |
| Select a trait                      | `--filter-trait "Category=Unit"`                                                                                   |
| Exclude matching tests              | `--filter-not-class`, `--filter-not-method`, `--filter-not-namespace`, or `--filter-not-trait`, followed by values |
| Show xUnit progress and diagnostics | `--xunit-info --xunit-diagnostics on`                                                                              |
| Show live test output               | `--show-live-output on`                                                                                            |
| Write xUnit's TRX report            | `--report-xunit-trx --report-xunit-trx-filename tests.trx`                                                         |

Multiple values can follow one filter switch, such as
`--filter-class "Namespace.First" "Namespace.Second"`. Do not substitute native xUnit flags like
`-class`, or VSTest's `--filter`, for these options.

Confirm that the intended tests ran; an empty selection is not evidence of success. Preserve nonzero
exit codes and investigate failures rather than switching runners or suppressing them.

## Configuration and troubleshooting

- Use `dotnet test --project tests/OssmSharp.Web.Api.Tests/OssmSharp.Web.Api.Tests.csproj -?` to
  inspect the options registered by that project's installed framework and extensions. For an
  unrecognized option, check this help and resolved versions before changing packages.
- `testconfig.json` configuration applies only in MTP mode. Follow the
  [xUnit configuration schema](https://xunit.net/docs/testconfig-json-mtp); do not assume it also
  configures the native xUnit runner.
- Report filename options accept a basename, not a path. Set the destination with
  `--results-directory`; the default is `TestResults` under the output folder. Prefer explicit
  filenames for stable artifacts: generated names include the user, machine, and timestamp.
- Do not copy VSTest's `--logger trx` or Coverlet collection commands. Use xUnit's report options
  above or an MTP-compatible extension. Microsoft's `--report-trx` requires its TRX reporting
  extension; it is not the same option as `--report-xunit-trx`. Follow the
  [MTP coverage guide](https://xunit.net/docs/getting-started/v3/code-coverage-with-mtp) when
  coverage is requested.
- Test Explorer integration is automatic in MTP-capable Visual Studio versions. Diagnose IDE
  compatibility rather than disabling MTP server capability or adding VSTest adapters to work around
  discovery failures.

For SDK integration details, see
[Microsoft's `dotnet test` modes and migration guidance](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test#mtp-mode-of-dotnet-test).
