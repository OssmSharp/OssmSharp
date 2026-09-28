#Requires -Version 7.0
<#
.SYNOPSIS
Publishes the CLI as a self-contained application for Windows x64 or macOS ARM64.
.DESCRIPTION
Selects the win-x64 or osx-arm64 profile from the host OS and defaults to Release.
Writes a binlog under .artifacts. macOS requires the .NET macOS workload and a
compatible Xcode installation; keep the entire published .app bundle together.
Existing publish output is updated. Use -WhatIf to preview without publishing.
Returns the dotnet publish exit code.
.PARAMETER Debug
Publishes a Debug build when explicitly enabled instead of the default Release.
.EXAMPLE
.\make.ps1
.EXAMPLE
.\make.ps1 -Debug
.EXAMPLE
.\make.ps1 -WhatIf
.OUTPUTS
System.String. Build and publish output from dotnet.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Join-Path $PSScriptRoot 'src/OssmSharp.Cli/OssmSharp.Cli.csproj'
$configuration = if ($PSBoundParameters['Debug']) { 'Debug' } else { 'Release' }
$profile = if ($IsWindows) {
    'win-x64'
} elseif ($IsMacOS) {
    'osx-arm64'
} else {
    throw 'Publishing is supported only on Windows or macOS.'
}
$publishArguments = @(
    'publish', $project,
    "-p:PublishProfile=$profile",
    '-c', $configuration,
    '-bl:.artifacts/cli-publish-{}.binlog'
)

if ($PSCmdlet.ShouldProcess($project, "Publish self-contained $profile CLI ($configuration)")) {
    Push-Location -LiteralPath $PSScriptRoot
    try {
        & dotnet @publishArguments
        $publishExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    exit $publishExitCode
}
