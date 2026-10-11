# Building and releasing OssmSharp

The first release is stable `v0.1.0`, not a beta. A pushed stable version tag runs
the same build and package checks as pull requests, then publishes the validated
outputs to GitHub Releases and nuget.org.

## Version contract

`VersionPrefix` in [Directory.Build.props](../Directory.Build.props) is the shared
numeric version. Tags must match it exactly: version `0.1.0` requires `v0.1.0`.
Prerelease tags and leading-zero numeric components are rejected.

First-party application and library assemblies use:

- Assembly/file version: `0.1.0.0`.
- Informational version and `--version`: `0.1.0+abcdef0`.
- A build with staged, unstaged, or non-ignored untracked files:
  `0.1.0+abcdef0-private`.
- macOS bundle display/build version: `0.1.0`.
- NuGet package version: `0.1.0`, without commit metadata.

The seven-character lowercase hash comes from the checked-out commit, including
detached HEAD builds. Package repository metadata preserves the full hash.
Ignored generated output does not make a build private. Actual builds require
Git and a committed checkout; source ZIPs without Git metadata cannot invent a
source revision.

All six application executables support `--version` before starting services.
Test platform and third-party executables retain their own argument contracts.

## Local checks

Use .NET SDK 10.0.401, PowerShell 7.4 or later, and Pester 6.1.0. The existing
[global.json](../global.json) allows this SDK through `latestMinor` roll-forward;
CI pins the SDK used for the verified native build.

Windows builds require Windows x64 and the Windows targeting SDK. macOS builds
require Apple silicon, the macOS workload, and Xcode 27:

```sh
dotnet workload install macos
```

Run:

```powershell
pwsh -File ./.github/scripts/Build-Release.ps1
```

The script builds the solution, runs all three MTP test projects and Pester tests,
publishes both applications, and checks extracted archives. It writes only
generated output under `.artifacts/release/<runtime>/`; rerunning replaces that
runtime's output. It does not install tools or publish.

The macOS build also creates the complete NuGet package with
`PackAllPlatforms=true`. It includes both runtime assemblies and XML documentation
for all three targets, but no dependency on a separate `OssmSharp.Runtime` package.
The package includes the [library README](../src/OssmSharp.Runtime.Oem/README.md).

CI uses `windows-2025` and `xcode-27`. GitHub currently labels the Xcode 27 runner
preview; the product release remains stable. The regular macOS 26 runner does not
provide the required Xcode version. See
[the official image details](https://github.com/actions/runner-images/blob/main/images/macos/xcode-27-arm64-Readme.md).

Download checks cover exact version output, macOS bundle versions and signatures,
CLI help, all three development health endpoints, and both panels' production and
development assets. They also verify health endpoints remain absent in production.
Readiness comes from listener events, not sleeps. Checks run outside the
source checkout with shared-runtime lookup pointed at a nonexistent directory.
The combined web host defaults to `logs/` in the working directory, outside the
macOS bundle. `OSSM:FileLogging:Directory` still overrides it. Checks exercise both
the default and configured directories and verify the bundle signature after use.
The macOS host uses the runtime's saved launch directory because native startup
changes the current directory to the bundle.
They do not connect to hardware or prove Bluetooth permissions or device operation.
Perform those checks manually on supported Windows and macOS machines before
the first release.

For source development, [run-web.ps1](../run-web.ps1) still builds and starts the
web application; [make.ps1](../make.ps1) publishes or runs the CLI.

## One-time account setup

Complete these steps before pushing a release tag:

1. Make `argium/OssmSharp` public so users can access the AGPL source, including
   its exact tagged version. The workflow refuses private source publication.
2. Enable Actions and run [Validate](../.github/workflows/validate.yml). Configure
   branch protection for `main` to require both native checks. Select their actual
   displayed names after the first run. These are account-side settings; the
   workflow does not configure them.
3. Protect `v*` tags against unauthorized creation, update, or deletion. Release
   tags must point to commits reachable from `origin/main`.
4. Create a GitHub environment named `release`, restrict it to version tags, and
   add any required approval rules. Only the publication job uses write and OIDC
   permissions.
5. Set repository variable `NUGET_USER` to the NuGet account username.
6. Create a [NuGet trusted publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   for owner `argium`, repository `OssmSharp`, workflow filename `release.yml`
   (not its full path), and environment `release`.
7. Scope that policy to `OssmSharp.Runtime.Oem`, permitting creation of the new
   package for the first release and new versions thereafter. Choose the package
   owner account or organization deliberately. Confirm the package ID is still
   available; a prior HTTP 404 is not a reservation.

No long-lived NuGet API key is needed. `NuGet/login` obtains a short-lived key just
before publication. Do not put it in source files or build arguments that are
logged. PR jobs cannot access publishing credentials.

The [license](../LICENSE) is AGPL-3.0-only. Every download includes it and a source
commit link. The package includes license text, an SPDX expression, its readme,
and repository commit metadata. Third-party dependencies retain their licenses.

Downloads are not Developer ID signed or notarized. macOS bundles retain native
ad-hoc signing. Windows and macOS may warn when opening downloads; the user setup
guide explains this limitation.

## Release procedure

1. Update `VersionPrefix` and the package-version examples when preparing a new
   version. Merge through a PR with all required checks passing.
2. Review the version and perform the documented hardware checks. Ensure
   `origin/main` is current and the checkout is clean.
3. Create and push the matching tag:

```sh
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "OssmSharp 0.1.0"
git push origin v0.1.0
```

4. Monitor [Release](../.github/workflows/release.yml). The tag's exact commit is
   rebuilt and checked. It must be on `main`, clean, and match the shared version.
5. Verify the public GitHub release and NuGet listing after publication. Download
   the archives and compare their SHA-256 values against the included checksum
   files. A queued job or locally passing build is not proof of publication.

The final stable GitHub release contains two Windows ZIPs, two macOS tar.gz
archives, the single nupkg, and the two checksum files. It is not marked prerelease.

## Failures and recovery

The GitHub and NuGet services are not transactional. Publication first creates a
GitHub draft with the validated assets, then pushes NuGet, then makes the draft a
stable public release. Runs for the same tag are serialized and are not cancelled
in mid-publication.

- **Build or validation fails:** no publication runs. Fix the cause through a PR.
  Use a new version/tag if a tagged release must change; do not move an existing
  public tag.
- **GitHub draft creation fails:** NuGet has not been pushed. Inspect conflicting
  releases, permissions, and availability before retrying.
- **NuGet push fails after draft creation:** inspect whether NuGet accepted the
  version. If it did not, resolve authentication/policy errors and either publish
  the exact validated package manually or remove the failed draft before rerunning
  the same unchanged tag.
- **NuGet succeeds but final GitHub publication fails:** do not rebuild or push
  the version again. Verify the existing draft's assets and checksums, then finish
  publishing that draft with `gh release edit v0.1.0 --draft=false --prerelease=false --latest`.
- **A published package or asset is wrong:** prepare a new version. The workflow
  fails on existing versions rather than silently skipping or replacing them.

Test results are retained for seven days. Build binlogs remain in generated build
output for local diagnosis; CI does not upload binlogs or application logs because
they can include configuration or payloads. Never upload a complete checkout as a
diagnostic artifact.
