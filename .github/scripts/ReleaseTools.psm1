Set-StrictMode -Version Latest

function Invoke-CheckedCommand {
    <#
    .SYNOPSIS
    Runs a command and preserves a nonzero exit as a failure.
    .DESCRIPTION
    Captures stdout and stderr. Failure messages include only the last 40 output
    lines. Arguments are not logged because they may contain credentials.
    .PARAMETER FileName
    The command to run.
    .PARAMETER Arguments
    Individual arguments, without shell quoting or command expressions.
    .EXAMPLE
    Invoke-CheckedCommand git @('rev-parse', 'HEAD')
    .OUTPUTS
    System.String. Captured command output.
    #>
    [CmdletBinding()]
    param([string] $FileName, [string[]] $Arguments)

    $output = & $FileName @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Select-Object -Last 40) -join [Environment]::NewLine
        throw "$FileName failed with exit code ${LASTEXITCODE}:$([Environment]::NewLine)$detail"
    }
    return $output
}

function Get-ReleaseMetadata {
    <#
    .SYNOPSIS
    Reads the shared version and current Git source state.
    .DESCRIPTION
    An optional tag requests release validation: stable version, matching version
    setting, clean checkout, and tag at HEAD.
    Does not modify Git refs or publish anything.
    .PARAMETER RepositoryRoot
    The source checkout containing Directory.Build.props.
    .PARAMETER Tag
    An exact vX.Y.Z release tag. Omit for local build metadata.
    .EXAMPLE
    Get-ReleaseMetadata -RepositoryRoot . -Tag v0.1.0
    .OUTPUTS
    PSCustomObject. Version, Commit, ShortCommit, IsDirty, and InformationalVersion.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,
        [string] $Tag
    )

    [xml] $settings = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
    $version = $settings.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
    $versionPattern = '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
    if ($version -cnotmatch "^$versionPattern$") {
        throw "VersionPrefix must be a stable X.Y.Z version; found '$version'."
    }
    $commit = (Invoke-CheckedCommand git @('-C', $RepositoryRoot, 'rev-parse', 'HEAD') | Out-String).Trim()
    if ($commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Cannot determine the source revision.'
    }
    $changes = @(Invoke-CheckedCommand git @('-C', $RepositoryRoot, 'status', '--porcelain=v1', '--untracked-files=normal'))
    $dirty = $changes.Count -ne 0
    $shortCommit = $commit.Substring(0, 7)
    $informationalVersion = "$version+$shortCommit"
    if ($dirty) {
        $informationalVersion += '-private'
    }
    if ($Tag) {
        if ($Tag -cnotmatch "^v$versionPattern$" -or $Tag -cne "v$version") {
            throw "Release tag '$Tag' must exactly match v$version; prerelease tags are not accepted."
        }
        if ($dirty) {
            throw 'Release requires a clean Git checkout, including staged and untracked files.'
        }
        $tagCommit = (Invoke-CheckedCommand git @('-C', $RepositoryRoot, 'rev-parse', "refs/tags/$Tag^{commit}") | Out-String).Trim()
        if ($tagCommit -cne $commit) {
            throw "Release tag '$Tag' does not identify the checked-out commit."
        }
    }
    return [pscustomobject] @{
        Version = $version
        Commit = $commit
        ShortCommit = $shortCommit
        IsDirty = $dirty
        InformationalVersion = $informationalVersion
    }
}

function Assert-ApplicationVersion {
    <#
    .SYNOPSIS
    Checks a published application without starting its services.
    .DESCRIPTION
    Requires exact version output, one newline, no stderr, and exit zero.
    Uses an isolated working directory and an unusable DOTNET_ROOT so that a
    self-contained app cannot silently depend on an installed shared runtime.
    .PARAMETER Executable
    The native application executable.
    .PARAMETER ExpectedVersion
    The complete informational version.
    .PARAMETER WorkingDirectory
    An isolated directory outside the source checkout.
    .EXAMPLE
    Assert-ApplicationVersion ./OssmSharp.Cli '0.1.0+abcdef0' /tmp/check
    .OUTPUTS
    None. Throws on failure.
    #>
    [CmdletBinding()]
    param([string] $Executable, [string] $ExpectedVersion, [string] $WorkingDirectory)

    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.ArgumentList.Add('--version')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ROOT'] = Join-Path $WorkingDirectory 'no-shared-runtime'
    $start.Environment['DOTNET_ROOT_ARM64'] = $start.Environment['DOTNET_ROOT']
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            throw "Version command did not exit: $Executable"
        }
        if ($process.ExitCode -ne 0 -or $stderr.GetAwaiter().GetResult() -cne '' -or
            $stdout.GetAwaiter().GetResult() -cne "$ExpectedVersion$([Environment]::NewLine)") {
            throw "Version command did not return exactly '$ExpectedVersion' with exit zero and empty stderr: $Executable"
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function Test-ReleaseArtifacts {
    <#
    .SYNOPSIS
    Checks that the expected release downloads exist.
    .DESCRIPTION
    Requires four native archives, one package, and both checksum files.
    Artifacts come from the native jobs in the same workflow run.
    .PARAMETER Directory
    The downloaded artifacts from both native jobs.
    .PARAMETER Metadata
    The version returned by Get-ReleaseMetadata.
    .EXAMPLE
    Test-ReleaseArtifacts .artifacts/release-input $metadata
    .OUTPUTS
    System.String. Absolute paths of the release assets and checksums.
    #>
    [CmdletBinding()]
    param([string] $Directory, [pscustomobject] $Metadata)

    $directoryPath = (Resolve-Path -LiteralPath $Directory).Path
    $expected = @(
        "OssmSharp.Cli-$($Metadata.Version)-win-x64.zip",
        "OssmSharp.Web-$($Metadata.Version)-win-x64.zip",
        "OssmSharp.Cli-$($Metadata.Version)-osx-arm64.tar.gz",
        "OssmSharp.Web-$($Metadata.Version)-osx-arm64.tar.gz",
        "OssmSharp.Runtime.Oem.$($Metadata.Version).nupkg",
        'SHA256SUMS-win-x64.txt',
        'SHA256SUMS-osx-arm64.txt'
    )
    foreach ($name in $expected) {
        if (-not (Test-Path -LiteralPath (Join-Path $directoryPath $name) -PathType Leaf)) {
            throw "Missing release output: $name"
        }
    }
    return $expected | ForEach-Object { Join-Path $directoryPath $_ }
}

Export-ModuleMember -Function Invoke-CheckedCommand, Get-ReleaseMetadata, Assert-ApplicationVersion, Test-ReleaseArtifacts
