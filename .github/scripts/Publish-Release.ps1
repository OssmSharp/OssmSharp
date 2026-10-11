#Requires -Version 7.4
<#
.SYNOPSIS
Publishes the exact validated tag artifacts to NuGet and GitHub.
.DESCRIPTION
Requires public source, a clean matching tag on origin/main, and matching artifact
provenance and checksums. GH_TOKEN and the short-lived NUGET_API_KEY must be in the
environment. Creates a GitHub draft, pushes NuGet, then makes the release public.
A failure can leave a draft or an already-published NuGet version; see
docs/RELEASING.md for recovery. Published versions are never silently replaced.
Use -WhatIf for validation without publication. This script does not create tags.
.PARAMETER Tag
The stable vX.Y.Z tag to release.
.PARAMETER Directory
The directory containing both platforms' validated outputs from the same run.
.EXAMPLE
./.github/scripts/Publish-Release.ps1 -Tag v0.1.0 -Directory .artifacts/release-input -WhatIf
.OUTPUTS
System.String. Publishing diagnostics, never credentials.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string] $Tag,
    [Parameter(Mandatory)][string] $Directory
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = Split-Path (Split-Path $PSScriptRoot)
$metadata = Get-ReleaseMetadata $root -Tag $Tag
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$assets = @(Test-ReleaseArtifacts $directoryPath $metadata)
$repo = Invoke-CheckedCommand gh @('repo', 'view', 'argium/OssmSharp', '--json', 'visibility') | ConvertFrom-Json
if ($repo.visibility -ne 'PUBLIC') {
    throw 'The source repository must be public before publication.'
}
$index = Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/ossmsharp.runtime.oem/index.json" -SkipHttpErrorCheck
if ($index.StatusCode -eq 200) {
    if (($index.Content | ConvertFrom-Json).versions -contains $metadata.Version) {
        throw "NuGet version $($metadata.Version) already exists. Do not rebuild or replace it."
    }
}
elseif ($index.StatusCode -ne 404) {
    throw "Cannot check existing NuGet versions (HTTP $($index.StatusCode))."
}
if (-not $PSCmdlet.ShouldProcess("GitHub $Tag and NuGet $($metadata.Version)", 'Publish validated artifacts')) {
    return
}
if ([string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)) {
    throw 'NUGET_API_KEY must contain the short-lived key from NuGet trusted publishing.'
}
$notes = @"
Download the web app for Windows x64 or Apple silicon macOS, extract the entire
archive, and follow the [setup instructions](https://github.com/argium/OssmSharp/blob/$Tag/README.md).
The downloads include everything needed to run; no .NET installation is required.

Separate command-line downloads are also available. Use --version to check your copy.
These downloads are not Developer ID signed or notarized; macOS may ask you to allow opening them.

OssmSharp is licensed under AGPL-3.0-only.
[Source for this release](https://github.com/argium/OssmSharp/tree/$($metadata.Commit)).
The device library is available on [NuGet](https://www.nuget.org/packages/OssmSharp.Runtime.Oem/$($metadata.Version)).
"@
Invoke-CheckedCommand gh (@('release', 'create', $Tag, '--repo', 'argium/OssmSharp',
    '--verify-tag', '--draft', '--title', "OssmSharp $($metadata.Version)", '--notes', $notes) + $assets) | Out-Host
Invoke-CheckedCommand dotnet @('nuget', 'push',
    (Join-Path $directoryPath "OssmSharp.Runtime.Oem.$($metadata.Version).nupkg"),
    '--api-key', $env:NUGET_API_KEY, '--source', 'https://api.nuget.org/v3/index.json') | Out-Host
Invoke-CheckedCommand gh @('release', 'edit', $Tag, '--repo', 'argium/OssmSharp',
    '--draft=false', '--prerelease=false', '--latest') | Out-Host
