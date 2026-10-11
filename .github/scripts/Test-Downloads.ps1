#Requires -Version 7.4
<#
.SYNOPSIS
Checks the two native application archives built on this platform.
.DESCRIPTION
Extracts into an isolated temporary directory. Checks native version metadata,
exact version output, CLI help, web health, packaged assets, and default and
configured log directories. Verifies macOS bundle signatures before and after use.
Starts no device motion. Always stops its owned process and removes scratch data.
.PARAMETER Directory
The directory containing the CLI and web archives.
.PARAMETER Version
The shared numeric release version.
.PARAMETER InformationalVersion
The complete binary version including the source commit and optional -private.
.EXAMPLE
./.github/scripts/Test-Downloads.ps1 .artifacts/release/osx-arm64/downloads 0.1.0 '0.1.0+abcdef0'
.OUTPUTS
None. Throws on failure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $InformationalVersion
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot '../../tests/ReleaseSmoke.cs')
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$runtime = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { throw 'Windows or macOS is required.' }
$extension = if ($IsWindows) { 'zip' } else { 'tar.gz' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) "OssmSharp-download-check-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
Push-Location $scratch
try {
    foreach ($application in @('OssmSharp.Cli', 'OssmSharp.Web')) {
        $archive = Join-Path $Directory "$application-$Version-$runtime.$extension"
        $extracted = Join-Path $scratch $application
        New-Item -ItemType Directory -Path $extracted | Out-Null
        if ($IsWindows) {
            Expand-Archive -LiteralPath $archive -DestinationPath $extracted
            $executable = Join-Path $extracted "$application.exe"
            $nativeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
            if ($nativeVersion.FileVersion -cne "$Version.0" -or $nativeVersion.ProductVersion -cne $InformationalVersion) {
                throw "$application Windows version resources do not match this build."
            }
        }
        else {
            Invoke-CheckedCommand tar @('-xzf', $archive, '-C', $extracted) | Out-Null
            $bundle = Join-Path $extracted "$application.app"
            $executable = Join-Path $bundle "Contents/MacOS/$application"
            Invoke-CheckedCommand codesign @('--verify', '--deep', '--strict', $bundle) | Out-Null
            foreach ($key in @('CFBundleShortVersionString', 'CFBundleVersion')) {
                $value = (Invoke-CheckedCommand /usr/libexec/PlistBuddy @('-c', "Print :$key",
                    (Join-Path $bundle 'Contents/Info.plist')) | Out-String).Trim()
                if ($value -cne $Version) {
                    throw "$application bundle $key is '$value', not '$Version'."
                }
            }
        }
        foreach ($file in @('LICENSE', 'SOURCE.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $extracted $file) -PathType Leaf)) {
                throw "$application archive is missing $file."
            }
        }
        Assert-ApplicationVersion $executable $InformationalVersion $scratch
        if ($application -eq 'OssmSharp.Cli') {
            $help = Invoke-CheckedCommand $executable @('--help') | Out-String
            if (-not $help.Contains('Control an OSSM device from the command line.')) {
                throw 'The extracted CLI did not return its help.'
            }
        }
        else {
            [ReleaseSmoke]::CheckWebAsync($executable, $scratch).GetAwaiter().GetResult() | Out-Null
            if ($IsMacOS) {
                Invoke-CheckedCommand codesign @('--verify', '--deep', '--strict', $bundle) | Out-Null
            }
        }
    }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
