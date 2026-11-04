#Requires -Version 7.4
<#
.SYNOPSIS
Publishes the validated tag artifacts to NuGet and GitHub.
.DESCRIPTION
Requires a clean matching tag and the downloads from both native jobs in one run.
Publication has four stages: prepare a draft, upload assets, push NuGet, and publish
the draft. Retries retain existing assets and packages only when their content
matches. GH_TOKEN and, for a new package, NUGET_API_KEY must be in the environment.
Published releases are never modified; the workflow still publishes their NuGet
package. Use -WhatIf to check local inputs without publication. This script does not create tags.
.PARAMETER Stage
Run one publication stage, or All (the default).
.EXAMPLE
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $Tag,
    [string] $Directory,
    [ValidateSet('All', 'Prepare', 'Upload', 'NuGet', 'Publish')]
    [string] $Stage = 'All'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1')
$root = Split-Path (Split-Path $PSScriptRoot)
$metadata = Get-ReleaseMetadata $root -Tag $Tag
$repository = 'OssmSharp/OssmSharp'
$expectedAssetNames = @(Get-ExpectedReleaseArtifactNames $metadata)
$stages = if ($Stage -eq 'All') { @('Prepare', 'Upload', 'NuGet', 'Publish') } else { @($Stage) }
$assets = @()
$directoryPath = $null
if ($stages | Where-Object { $_ -ne 'Prepare' }) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { throw 'Directory is required for release asset stages.' }
    $directoryPath = (Resolve-Path -LiteralPath $Directory).Path
    $assets = @(Test-ReleaseArtifacts $directoryPath $metadata)
}

function Get-PackageContentHashes {
    param([string] $Path)

    # NuGet adds a repository signature; compare payloads, not ZIP bytes or timestamps.
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries | Sort-Object FullName) {
            if ($entry.FullName -ceq '.signature.p7s') { continue }
            $stream = $entry.Open()
            try {
                "$($entry.FullName):$([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)))"
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Get-ReleaseDetails {
    Invoke-CheckedCommand gh @('release', 'view', $Tag, '--repo', $repository,
        '--json', 'isDraft,assets') | ConvertFrom-Json
}

function Assert-ReleaseAssets {
    param([pscustomobject] $Release)

    if (-not $Release.isDraft) { return }

    foreach ($name in $expectedAssetNames) {
        $matches = @($Release.assets | Where-Object name -CEQ $name)
        if ($matches.Count -ne 1) {
            throw "Draft release is missing required asset: $name."
        }
        if ([string]::IsNullOrWhiteSpace($matches[0].digest)) {
            throw "Existing release asset has no digest: $name."
        }
        $assetPath = Join-Path $directoryPath $name
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($matches[0].digest -cne $digest) {
            throw "Existing release asset differs: $name. Reuse the original workflow artifacts."
        }
    }
}

foreach ($currentStage in $stages) {
    if (-not $PSCmdlet.ShouldProcess("$repository $Tag", "Release stage: $currentStage")) { continue }
    switch ($currentStage) {
        Prepare {
            # Listing releases distinguishes a missing tag from authentication or network failure.
            $query = ".[] | select(.tag_name == `"$Tag`")"
            $release = Invoke-CheckedCommand gh @('api', '--paginate', "repos/$repository/releases",
                '--jq', $query) | ConvertFrom-Json
            if ($release) {
                if (-not $release.draft) {
                    Write-Warning "Release $Tag is already published; leaving its GitHub assets unchanged."
                }
                break
            }
            $notes = @"
Download the web app or CLI for Windows x64 or Apple silicon macOS. Extract the
entire archive and follow the [setup instructions](https://github.com/$repository/blob/$Tag/README.web.md).
No .NET installation is required. Downloads are not Developer ID signed or notarized.

[Source](https://github.com/$repository/tree/$($metadata.Commit)) · AGPL-3.0-only ·
[Device library on NuGet](https://www.nuget.org/packages/OssmSharp.Runtime.Oem/$($metadata.Version))
"@
            Invoke-CheckedCommand gh @('release', 'create', $Tag, '--repo', $repository,
                '--verify-tag', '--draft', '--title', "OssmSharp $($metadata.Version)",
                '--generate-notes', '--notes', $notes) | Out-Host
            $release = Get-ReleaseDetails
            if (-not $release.isDraft) { throw "GitHub release $Tag was not created as a draft." }
        }
        Upload {
            $release = Get-ReleaseDetails
            if (-not $release.isDraft) {
                Write-Warning "Release $Tag is already published; skipping GitHub asset uploads."
                break
            }
            foreach ($asset in $assets) {
                $name = [IO.Path]::GetFileName($asset)
                $existing = @($release.assets | Where-Object name -CEQ $name)
                if ($existing) {
                    if ($existing.Count -ne 1 -or [string]::IsNullOrWhiteSpace($existing[0].digest)) {
                        throw "Existing release asset has invalid digest metadata: $name."
                    }
                    $digest = 'sha256:' + (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($existing[0].digest -cne $digest) {
                        throw "Existing release asset differs: $name. Reuse the original workflow artifacts."
                    }
                }
                else {
                    Invoke-CheckedCommand gh @('release', 'upload', $Tag, $asset, '--repo', $repository) | Out-Host
                }
            }
            Assert-ReleaseAssets (Get-ReleaseDetails)
        }
        NuGet {
            Assert-ReleaseAssets (Get-ReleaseDetails)
            $package = Join-Path $directoryPath "OssmSharp.Runtime.Oem.$($metadata.Version).nupkg"
            $url = "https://api.nuget.org/v3-flatcontainer/ossmsharp.runtime.oem/$($metadata.Version)/ossmsharp.runtime.oem.$($metadata.Version).nupkg"
            $download = [IO.Path]::GetTempFileName()
            try {
                $response = Invoke-WebRequest $url -OutFile $download -PassThru -SkipHttpErrorCheck
                if ($response.StatusCode -eq 200) {
                    $difference = Compare-Object @(Get-PackageContentHashes $package) @(Get-PackageContentHashes $download) -CaseSensitive
                    if ($difference) { throw "NuGet version $($metadata.Version) already exists with different content." }
                    Write-Information "NuGet version $($metadata.Version) already contains this package."
                }
                elseif ($response.StatusCode -eq 404) {
                    if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
                        throw 'NUGET_API_KEY must contain the short-lived key from NuGet trusted publishing.'
                    }
                    Invoke-CheckedCommand dotnet @('nuget', 'push', $package,
                        '--api-key', $env:NUGET_API_KEY, '--source', 'https://api.nuget.org/v3/index.json') | Out-Host
                }
                else { throw "Cannot check existing NuGet version (HTTP $($response.StatusCode))." }
            }
            finally { Remove-Item -LiteralPath $download -Force }
        }
        Publish {
            $release = Get-ReleaseDetails
            if ($release.isDraft) {
                Assert-ReleaseAssets $release
                Invoke-CheckedCommand gh @('release', 'edit', $Tag, '--repo', $repository,
                    '--draft=false', '--prerelease=false', '--latest') | Out-Host
                if ((Get-ReleaseDetails).isDraft) { throw "GitHub release $Tag is still a draft after publishing." }
            }
            else {
                Write-Information "Release $Tag is already published; no GitHub release changes are needed."
            }
        }
    }
}
