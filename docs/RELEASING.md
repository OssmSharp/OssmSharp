# Releasing

## One-time setup

- Make tagged source public for AGPL compliance.
- Protect `main` with the required [Validate](../.github/workflows/validate.yml)
  checks, and protect `v*` tags.
- Create the `release` environment, restricted to version tags.
- Set repository variable `NUGET_USER`.
- Configure [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
  for `OssmSharp.Runtime.Oem`, workflow `release.yml`, and environment `release`.
  The scripts currently target `argium/OssmSharp`; match the policy to that
  repository.

## Check locally

Use the SDK in [global.json](../global.json), PowerShell 7.4+, and Pester 6+.
Native builds need Windows x64 or Apple silicon with Xcode 27 and the .NET
macOS workload.

```powershell
pwsh -File ./.github/scripts/Build-Release.ps1
```

Outputs: `.artifacts/release/<runtime>/`.
Check Bluetooth permissions and device operation on both platforms separately;
automated checks do not use hardware.

## Publish

Update `VersionPrefix` in [Directory.Build.props](../Directory.Build.props)
and version examples, then merge with checks passing. From clean, current `main`,
push a matching stable tag:

```sh
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "OssmSharp 0.1.0"
git push origin v0.1.0
```

Watch [Release](../.github/workflows/release.yml), then verify the public GitHub
release, NuGet listing, and download checksums.

## Recover

Fix the cause, then **rerun failed jobs** in the original run. Do not rebuild
successful native jobs: archives can differ even at the same commit.

Reuse original downloads. Artifacts expire after seven days; if originals are
unavailable, release a new version.

For manual recovery with credentials configured:

```powershell
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input -WhatIf
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input
```

Never move a public tag or replace published content. Use a new version.
Diagnose CI failures from run logs and artifacts; do not upload credentials,
payloads, or a full checkout.
