#Requires -Version 7.0
<#
.SYNOPSIS
Publishes or runs the CLI for Windows x64 or macOS ARM64.
.DESCRIPTION
Selects the win-x64 or osx-arm64 profile from the host OS and defaults to Release.
Writes a binlog under .artifacts. macOS requires the .NET macOS workload and a
compatible Xcode installation; keep the entire published .app bundle together.
Publishing updates existing output. Run starts the existing native executable
without publishing; publish first if it is missing. Use -WhatIf to preview.
Returns the publish or CLI exit code.
.PARAMETER Command
Publish (the default) or run the existing CLI.
.PARAMETER Arguments
Arguments forwarded to the CLI when using run.
.PARAMETER Debug
Selects a Debug build instead of the default Release.
.EXAMPLE
.\make.ps1
.EXAMPLE
.\make.ps1 -Debug
.EXAMPLE
.\make.ps1 -WhatIf
.EXAMPLE
.\make.ps1 run --help
.EXAMPLE
.\make.ps1 run prompt
.OUTPUTS
System.String. Build output or CLI output.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [ValidateSet('publish', 'run')]
    [string] $Command = 'publish',

    [Parameter(Position = 1, ValueFromRemainingArguments)]
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Command -eq 'publish' -and $Arguments.Count -gt 0) {
    throw 'CLI arguments are supported only with run.'
}
$project = Join-Path $PSScriptRoot 'src/OssmSharp.Cli/OssmSharp.Cli.csproj'
$configuration = if ($PSBoundParameters['Debug']) { 'Debug' } else { 'Release' }
$profile = if ($IsWindows) {
    'win-x64'
} elseif ($IsMacOS) {
    'osx-arm64'
} else {
    throw 'Publishing and running are supported only on Windows or macOS.'
}
if ($PSCmdlet.ShouldProcess($project, "$Command self-contained $profile CLI ($configuration)")) {
    Push-Location -LiteralPath $PSScriptRoot
    try {
        $framework = (& dotnet msbuild $project -getProperty:NativeTargetFramework | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not determine the native target framework.'
        }
        if ($Command -eq 'run') {
            $metadataArguments = @(
                'msbuild', $project,
                "-p:TargetFramework=$framework",
                "-p:PublishProfile=$profile",
                "-p:Configuration=$configuration",
                '-getProperty:PublishDir,AssemblyName,MSBuildProjectDirectory,_NativeExecutable'
            )
            if ($IsMacOS) {
                $metadataArguments += '-target:_GetNativeExecutableName'
            }
            $metadata = & dotnet @metadataArguments
            if ($LASTEXITCODE -ne 0) {
                throw 'Could not determine the CLI executable path.'
            }
            $properties = ($metadata | ConvertFrom-Json).Properties
            $executable = if ($IsMacOS) {
                Join-Path $properties.MSBuildProjectDirectory $properties._NativeExecutable
            } else {
                Join-Path $properties.PublishDir "$($properties.AssemblyName).exe"
            }
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "CLI executable is missing. Run ./make.ps1 with the same configuration first."
            }
            & $executable @Arguments
        } else {
            $publishArguments = @(
                'publish', $project,
                '-f', $framework,
                "-p:PublishProfile=$profile",
                '-c', $configuration,
                '-bl:.artifacts/cli-publish-{}.binlog'
            )
            & dotnet @publishArguments
        }
        $commandExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    exit $commandExitCode
}
