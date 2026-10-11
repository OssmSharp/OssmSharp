#Requires -Version 7.4
<#
.SYNOPSIS
Publishes the validated tag artifacts to NuGet and GitHub.
.DESCRIPTION
Requires a clean matching tag and the downloads from both native jobs in one run.
Publication has four stages: prepare a draft, upload assets, push NuGet, and publish
the draft. Retries retain existing assets and packages only when their content
matches. GH_TOKEN and, for a new package, NUGET_API_KEY must be in the environment.
Use -WhatIf to check local inputs without publication. This script does not create tags.
.PARAMETER Stage
Run one publication stage, or All (the default).
.EXAMPLE
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $Directory,
    [ValidateSet('All', 'Prepare', 'Upload', 'NuGet', 'Publish')]
    [string] $Stage = 'All'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1')
$root = Split-Path (Split-Path $PSScriptRoot)
$metadata = Get-ReleaseMetadata $root -Tag $Tag
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$assets = @(Test-ReleaseArtifacts $directoryPath $metadata)
$repository = 'argium/OssmSharp'

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

$stages = if ($Stage -eq 'All') { @('Prepare', 'Upload', 'NuGet', 'Publish') } else { @($Stage) }
foreach ($currentStage in $stages) {
    if (-not $PSCmdlet.ShouldProcess("$repository $Tag", "Release stage: $currentStage")) { continue }
    switch ($currentStage) {
        Prepare {
            # Listing releases distinguishes a missing tag from authentication or network failure.
            $query = ".[] | select(.tag_name == `"$Tag`")"
            $release = Invoke-CheckedCommand gh @('api', '--paginate', "repos/$repository/releases",
                '--jq', $query) | ConvertFrom-Json
            if ($release) { break }
            $notes = @"
Download the web app or CLI for Windows x64 or Apple silicon macOS. Extract the
entire archive and follow the [setup instructions](https://github.com/$repository/blob/$Tag/README.md).
No .NET installation is required. Downloads are not Developer ID signed or notarized.

[Source](https://github.com/$repository/tree/$($metadata.Commit)) · AGPL-3.0-only ·
[Device library on NuGet](https://www.nuget.org/packages/OssmSharp.Runtime.Oem/$($metadata.Version))
"@
            Invoke-CheckedCommand gh @('release', 'create', $Tag, '--repo', $repository,
                '--verify-tag', '--draft', '--title', "OssmSharp $($metadata.Version)",
                '--generate-notes', '--notes', $notes) | Out-Host
        }
        Upload {
            $release = Invoke-CheckedCommand gh @('release', 'view', $Tag, '--repo', $repository,
                '--json', 'isDraft,assets') | ConvertFrom-Json
            foreach ($asset in $assets) {
                $name = [IO.Path]::GetFileName($asset)
                $existing = $release.assets | Where-Object name -CEQ $name
                if ($existing) {
                    $digest = 'sha256:' + (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($existing.digest -cne $digest) {
                        throw "Existing release asset differs: $name. Reuse the original workflow artifacts."
                    }
                }
                elseif ($release.isDraft) {
                    Invoke-CheckedCommand gh @('release', 'upload', $Tag, $asset, '--repo', $repository) | Out-Host
                }
                else { throw "Published release is missing $name; prepare a new version." }
            }
        }
        NuGet {
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
            Invoke-CheckedCommand gh @('release', 'edit', $Tag, '--repo', $repository,
                '--draft=false', '--prerelease=false', '--latest') | Out-Host
        }
    }
}
