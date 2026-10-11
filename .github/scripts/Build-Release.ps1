#Requires -Version 7.4
<#
.SYNOPSIS
Builds, tests, and packages the release outputs for this host.
.DESCRIPTION
Runs the same checks for pull requests and tags. Writes only generated files under
.artifacts/release/<runtime>, replacing that runtime's previous generated output.
Requires the repository's SDK, Pester, and native platform build tools.
Does not publish, install workloads, create tags, or change account settings.
.PARAMETER Tag
An optional stable release tag, which must match the version and clean checkout.
.EXAMPLE
pwsh -File ./.github/scripts/Build-Release.ps1
.EXAMPLE
pwsh -File ./.github/scripts/Build-Release.ps1 -Tag v0.1.0
.OUTPUTS
System.String. Build and verification diagnostics.
#>
[CmdletBinding()]
param([string] $Tag)

$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = Split-Path (Split-Path $PSScriptRoot)
$metadata = Get-ReleaseMetadata $root -Tag $Tag
$runtime = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { throw 'Windows or macOS is required.' }
$framework = if ($IsWindows) { 'net10.0-windows10.0.19041.0' } else { 'net10.0-macos27.0' }
$output = Join-Path $root ".artifacts/release/$runtime"
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
$downloads = Join-Path $output 'downloads'
New-Item -ItemType Directory -Path $downloads, (Join-Path $output 'results') | Out-Null
Push-Location $root
try {
    Write-Information "Building $runtime at $($metadata.InformationalVersion)."
    Invoke-CheckedCommand dotnet @('build', 'OssmSharp.slnx', '-c', 'Release',
        '-p:ContinuousIntegrationBuild=true', '--verbosity', 'quiet',
        "-bl:$output/build-{}.binlog") | Out-Host
    Invoke-CheckedCommand dotnet @('test', '--solution', 'OssmSharp.slnx', '-c', 'Release',
        '--no-build', '--no-restore', '--report-xunit-trx',
        '--results-directory', (Join-Path $output 'results'), '--verbosity', 'quiet',
        "-bl:$output/tests-{}.binlog") | Out-Host
    $pester = New-PesterConfiguration
    $pester.Run.Path = Join-Path $root 'tests'
    $pester.Run.PassThru = $true
    $pester.TestResult.Enabled = $true
    $pester.TestResult.OutputPath = Join-Path $output 'results/pester.xml'
    $pester.Output.Verbosity = 'Detailed'
    $result = Invoke-Pester -Configuration $pester
    if ($result.FailedCount -ne 0 -or $result.PassedCount -eq 0) {
        throw "Pester checks failed or selected no tests ($($result.FailedCount) failures)."
    }

    foreach ($application in @('OssmSharp.Cli', 'OssmSharp.Web')) {
        $project = Join-Path $root "src/$application/$application.csproj"
        $stage = Join-Path $output "staging/$application"
        New-Item -ItemType Directory -Path $stage | Out-Null
        $publish = if ($IsWindows) { $stage } else { Join-Path $output "publish/$application" }
        Invoke-CheckedCommand dotnet @('publish', $project, '-c', 'Release', '-f', $framework,
            "-p:PublishProfile=$runtime", "-p:PublishDir=$publish/",
            '-p:ContinuousIntegrationBuild=true', '--verbosity', 'quiet',
            "-bl:$output/publish-{}.binlog") | Out-Host
        if ($IsMacOS) {
            $bundlePath = (Invoke-CheckedCommand dotnet @('msbuild', $project,
                "-p:TargetFramework=$framework", '-p:Configuration=Release', "-p:PublishProfile=$runtime",
                '-target:_GetNativeExecutableName', '-getProperty:AppBundleDir',
                "-bl:$output/bundle-{}.binlog") | Out-String).Trim()
            $bundlePath = [IO.Path]::GetFullPath($bundlePath, (Split-Path $project))
            Invoke-CheckedCommand ditto @($bundlePath, (Join-Path $stage "$application.app")) | Out-Null
        }
        Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') -Destination $stage
        $source = @(
            "OssmSharp $($metadata.InformationalVersion)"
            'License: AGPL-3.0-only. See LICENSE.'
            "Base source commit: https://github.com/argium/OssmSharp/tree/$($metadata.Commit)"
            'Third-party dependencies retain their own licenses.'
        )
        if ($metadata.IsDirty) {
            $source += 'This private build also includes uncommitted source changes.'
        }
        $source | Set-Content (Join-Path $stage 'SOURCE.txt')
        $archiveName = "$application-$($metadata.Version)-$runtime"
        if ($IsWindows) {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $downloads "$archiveName.zip")
        }
        else {
            Invoke-CheckedCommand tar @('-czf', (Join-Path $downloads "$archiveName.tar.gz"), '-C', $stage, '.') | Out-Null
        }
    }
    & (Join-Path $PSScriptRoot 'Test-Downloads.ps1') $downloads $metadata.Version $metadata.InformationalVersion
    Write-Information "Extracted $runtime downloads passed."

    if ($IsMacOS) {
        Invoke-CheckedCommand dotnet @('pack', 'src/OssmSharp.Runtime.Oem/OssmSharp.Runtime.Oem.csproj',
            '-c', 'Release', '-p:PackAllPlatforms=true', '-p:ContinuousIntegrationBuild=true',
            '-o', $downloads, '--verbosity', 'quiet', "-bl:$output/pack-{}.binlog") | Out-Host
        Write-Information 'Cross-platform NuGet package created.'
    }
    $hashes = Get-ChildItem -LiteralPath $downloads -File | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
    $hashes | Set-Content (Join-Path $downloads "SHA256SUMS-$runtime.txt")
    $metadata | Add-Member -NotePropertyName Runtime -NotePropertyValue $runtime
    $metadata | ConvertTo-Json | Set-Content (Join-Path $downloads "manifest-$runtime.json")
    $final = Get-ReleaseMetadata $root -Tag $Tag
    if ($final.InformationalVersion -cne $metadata.InformationalVersion -or $final.Commit -cne $metadata.Commit) {
        throw 'Source version changed while building; discard these artifacts and rebuild.'
    }
    Write-Information "Validated outputs: $downloads"
}
finally {
    Pop-Location
}
