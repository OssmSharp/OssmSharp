# Build and release

A stable version tag runs the native build checks, then publishes validated
downloads to GitHub Releases and nuget.org. The first release is `v0.1.0`.

## Versions

Tags must match `VersionPrefix` in
[Directory.Build.props](../Directory.Build.props): `0.1.0` requires `v0.1.0`.
Prereleases and leading-zero components are rejected.

| Output | Example |
| --- | --- |
| Assembly and file version | `0.1.0.0` |
| Informational version and `--version` | `0.1.0+abcdef0` |
| Build with uncommitted changes | `0.1.0+abcdef0-private` |
| macOS bundle and NuGet version | `0.1.0` |

The short hash is seven lowercase characters from the checked-out commit,
including detached HEAD. Package metadata keeps the full hash. Ignored build
output does not make a build private. Builds require Git and a committed
checkout, not a source ZIP.

All six app executables accept `--version` before starting services. Test and
third-party executables keep their own arguments.

## Local checks

Use the SDK selected by [global.json](../global.json), PowerShell 7.4+, and
Pester 6+. CI pins the SDK for native builds. Windows needs x64 and the Windows
targeting SDK. macOS needs Apple silicon, Xcode 27, and the macOS workload:

```sh
dotnet workload install macos
```

```powershell
pwsh -File ./.github/scripts/Build-Release.ps1
```

The script builds the solution, runs MTP and Pester tests, publishes both apps,
and checks extracted archives. It replaces generated output for the current
runtime under `.artifacts/release/<runtime>/`. It neither installs tools nor
publishes releases.

The macOS build packs all three library targets with `PackAllPlatforms=true`.
The package bundles both runtime assemblies, XML documentation, and the
[package README](../src/OssmSharp.Runtime.Oem/README.md), with no separate
`OssmSharp.Runtime` package dependency. App archives include
[README.web.md](../README.web.md).

CI uses `windows-2025` and `xcode-27`. The Xcode runner is labeled preview;
the release is stable. The regular macOS 26 runner lacks Xcode 27.
See [runner details](https://github.com/actions/runner-images/blob/main/images/macos/xcode-27-arm64-Readme.md).

Download checks cover versions, bundle signatures, CLI help, panel assets,
development health endpoints, and their absence in production. Listener events
signal readiness. Checks run outside the checkout with shared-runtime lookup
pointed at a nonexistent directory.

The web host writes logs under the launch directory's `logs/`, outside the
macOS bundle. `OSSM:FileLogging:Directory` overrides it. Checks cover both paths
and recheck bundle signatures afterward.

These checks do not use hardware. Before the first release, check Bluetooth
permissions and device operation on supported Windows and macOS machines.
For development, [run-web.ps1](../run-web.ps1) starts the web app;
[make.ps1](../make.ps1) publishes or runs the CLI.

## Account setup

Before tagging:

1. Make the release repository public so users can access the exact AGPL source.
   The release scripts currently target `argium/OssmSharp`.
2. Enable Actions and run [Validate](../.github/workflows/validate.yml). Require
   both native checks in `main` branch protection, using their displayed names.
3. Protect `v*` tags from unauthorized creation, changes, and deletion.
4. Create the `release` environment, restrict it to version tags, and set
   approvals. Only publication gets write and OIDC permissions.
5. Set repository variable `NUGET_USER` to the NuGet username.
6. Create a [NuGet trusted publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   for owner `argium`, repository `OssmSharp`, workflow `release.yml`, and
   environment `release`.
7. Scope it to `OssmSharp.Runtime.Oem`, allowing first publication and later
   versions. Choose the owner deliberately and check package ID availability;
   an earlier 404 does not reserve it.

`NuGet/login` obtains a short-lived key. Never store it in source or logged
arguments. PR jobs cannot access publication credentials.

Every download includes the [AGPL-3.0-only license](../LICENSE) and source commit
link. The package also includes SPDX and repository metadata. Dependencies keep
their licenses.

Downloads are not Developer ID signed or notarized. macOS bundles use ad-hoc
signing. The [web guide](../README.web.md) explains opening warnings.

## Publish

1. Update `VersionPrefix` and version examples. Merge a PR with required checks.
2. Check hardware, confirm the version, and start from clean, current `main`.
3. Create and push the matching tag:

```sh
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "OssmSharp 0.1.0"
git push origin v0.1.0
```

4. Watch [Release](../.github/workflows/release.yml). It rebuilds and checks
   the exact tag commit in a clean checkout.
5. Check the public release and NuGet listing. Download archives and compare
   SHA-256 values with the checksum files.

The stable release contains two Windows ZIPs, two macOS tar.gz archives, one
nupkg, and two checksum files. GitHub generates the changelog.

## Recover publication

Publication creates or reuses a draft, uploads downloads, publishes NuGet,
then publishes the GitHub release. Runs for one tag are serialized and never
cancelled during publication.

Fix authentication or service failures, then **rerun failed jobs** in the
original run. Reuse its native build artifacts. Matching GitHub assets stay;
missing assets are added only to drafts. Existing NuGet content must match,
ignoring nuget.org's repository signature. Any mismatch stops publication.
A draft or NuGet version may remain after failure.

Do not rebuild successful native jobs: archives can differ at the same commit.
After artifacts expire in seven days, recover the original downloads or
prepare a new version.

For local recovery, configure credentials and use the original downloads:

```powershell
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input -WhatIf
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input
```

`-WhatIf` checks inputs without network access or publication.
`-Stage Prepare`, `Upload`, `NuGet`, and `Publish` run individual stages.
Complete upload and package checks before publishing the draft.

The script cannot add or replace assets on a published release. For incorrect
content or source changes, use a new version and tag; never move a public tag.

Test results last seven days. Binlogs stay in local generated output; CI omits
them and app logs because they may contain configuration or payloads.
Never upload a full checkout for diagnosis.
