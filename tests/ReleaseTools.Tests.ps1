BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleaseTools.psm1') -Force
    $global:OssmReleaseTestState = @{}
    $publishScript = Join-Path $PSScriptRoot '../.github/scripts/Publish-Release.ps1'

    function New-TestPackage {
        param([string] $Path, [string] $Payload = 'original', [switch] $Signed)

        $archive = [IO.Compression.ZipFile]::Open($Path, 'Create')
        try {
            $contents = @{ 'OssmSharp.Runtime.Oem.nuspec' = '<package />'; 'lib/net10.0/library.dll' = $Payload }
            if ($Signed) { $contents['.signature.p7s'] = 'repository signature' }
            foreach ($name in $contents.Keys) {
                $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
                try { $writer.Write($contents[$name]) }
                finally { $writer.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
}

Describe 'Release metadata' {
    BeforeEach {
        $fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $fixture | Out-Null
        '<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix></PropertyGroup></Project>' |
            Set-Content (Join-Path $fixture 'Directory.Build.props')
        Invoke-CheckedCommand git @('init', '--quiet', $fixture) | Out-Null
        Invoke-CheckedCommand git @('-C', $fixture, 'add', '.') | Out-Null
        Invoke-CheckedCommand git @('-C', $fixture, '-c', 'user.name=Release Test',
            '-c', 'user.email=release@example.invalid', '-c', 'commit.gpgsign=false',
            'commit', '--quiet', '-m', 'Fixture') | Out-Null
        Invoke-CheckedCommand git @('-C', $fixture, 'tag', 'v0.1.0') | Out-Null
    }

    It 'accepts a matching clean tag without a remote or main branch' {
        (Get-ReleaseMetadata $fixture -Tag v0.1.0).Version | Should -BeExactly '0.1.0'
    }

    It 'rejects a tag that disagrees with the shared version' {
        { Get-ReleaseMetadata $fixture -Tag v0.2.0 } | Should -Throw '*must exactly match*'
    }

    It 'rejects a release tag that does not identify HEAD' {
        Invoke-CheckedCommand git @('-C', $fixture, '-c', 'user.name=Release Test',
            '-c', 'user.email=release@example.invalid', '-c', 'commit.gpgsign=false',
            'commit', '--quiet', '--allow-empty', '-m', 'Next commit') | Out-Null

        { Get-ReleaseMetadata $fixture -Tag v0.1.0 } | Should -Throw '*checked-out commit*'
    }

    It 'rejects local release builds with uncommitted source' {
        'untracked' | Set-Content (Join-Path $fixture 'new.txt')

        { Get-ReleaseMetadata $fixture -Tag v0.1.0 } | Should -Throw '*clean Git checkout*'
    }
}

Describe 'Release downloads and publication' {
    BeforeEach {
        $directory = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory | Out-Null
        $metadata = [pscustomobject] @{ Version = '0.1.0'; Commit = 'a' * 40 }
        $names = @(
            'OssmSharp.Cli-0.1.0-win-x64.zip', 'OssmSharp.Web-0.1.0-win-x64.zip',
            'OssmSharp.Cli-0.1.0-osx-arm64.tar.gz', 'OssmSharp.Web-0.1.0-osx-arm64.tar.gz',
            'OssmSharp.Runtime.Oem.0.1.0.nupkg', 'SHA256SUMS-win-x64.txt', 'SHA256SUMS-osx-arm64.txt'
        )
        foreach ($assetName in $names) { 'download' | Set-Content (Join-Path $directory $assetName) }
        $package = Join-Path $directory 'OssmSharp.Runtime.Oem.0.1.0.nupkg'
        Remove-Item -LiteralPath $package
        New-TestPackage $package
        $global:OssmReleaseTestState.Release = [pscustomobject] @{ isDraft = $true; assets = @() }
        $global:OssmReleaseTestState.Release.assets = @($names | ForEach-Object {
            [pscustomobject] @{
                name = $_
                digest = 'sha256:' + (Get-FileHash (Join-Path $directory $_)).Hash.ToLowerInvariant()
            }
        })
        $global:OssmReleaseTestState.ExistingPackage = $null
        $global:OssmReleaseTestState.NuGetStatus = 404
        $savedKey = $env:NUGET_API_KEY
        $env:NUGET_API_KEY = 'test-key'

        Mock Get-ReleaseMetadata { $metadata }
        Mock Invoke-CheckedCommand {
            if ($FileName -eq 'gh' -and $Arguments[0] -eq 'api') {
                if ($global:OssmReleaseTestState.Release) {
                    return [pscustomobject] @{
                        draft = $global:OssmReleaseTestState.Release.isDraft
                        assets = $global:OssmReleaseTestState.Release.assets
                    } | ConvertTo-Json -Depth 5 -Compress
                }
            }
            elseif ($FileName -eq 'gh' -and $Arguments[1] -eq 'view') {
                if ($global:OssmReleaseTestState.Release) { return $global:OssmReleaseTestState.Release | ConvertTo-Json -Depth 5 -Compress }
            }
            elseif ($FileName -eq 'gh' -and $Arguments[1] -eq 'create') {
                $global:OssmReleaseTestState.Release = [pscustomobject] @{ isDraft = $true; assets = @() }
            }
            elseif ($FileName -eq 'gh' -and $Arguments[1] -eq 'upload') {
                $assetPath = $Arguments[3]
                $global:OssmReleaseTestState.Release.assets += [pscustomobject] @{
                    name = [IO.Path]::GetFileName($assetPath)
                    digest = 'sha256:' + (Get-FileHash -LiteralPath $assetPath).Hash.ToLowerInvariant()
                }
            }
            elseif ($FileName -eq 'gh' -and $Arguments[1] -eq 'edit') {
                $global:OssmReleaseTestState.Release.isDraft = $false
            }
        }
        Mock Invoke-WebRequest {
            if ($global:OssmReleaseTestState.ExistingPackage) { Copy-Item -LiteralPath $global:OssmReleaseTestState.ExistingPackage -Destination $OutFile }
            return [pscustomobject] @{ StatusCode = $global:OssmReleaseTestState.NuGetStatus }
        }
    }

    AfterEach { $env:NUGET_API_KEY = $savedKey }

    It 'accepts the complete downloads without manifests' {
        @(Test-ReleaseArtifacts $directory $metadata).Count | Should -Be 7
    }

    It 'rejects missing <Name> before any publication' -ForEach @(
        @{ Name = 'OssmSharp.Web-0.1.0-win-x64.zip' }
        @{ Name = 'OssmSharp.Runtime.Oem.0.1.0.nupkg' }
        @{ Name = 'SHA256SUMS-osx-arm64.txt' }
    ) {
        Remove-Item -LiteralPath (Join-Path $directory $Name)

        { & $publishScript -Tag v0.1.0 -Directory $directory -Confirm:$false } |
            Should -Throw "*Missing release output: $Name*"
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly
        Should -Invoke Invoke-WebRequest -Times 0 -Exactly
    }

    It 'previews publication without network access or credentials' {
        Remove-Item Env:NUGET_API_KEY

        & $publishScript -Tag v0.1.0 -Directory $directory -WhatIf

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly
        Should -Invoke Invoke-WebRequest -Times 0 -Exactly
    }

    It 'reuses an existing draft during preflight without requiring build artifacts' {
        & $publishScript -Tag v0.1.0 -Stage Prepare -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -eq 'create' }
    }

    It 'creates a draft during preflight when a tag has no release' {
        $global:OssmReleaseTestState.Release = $null

        & $publishScript -Tag v0.1.0 -Stage Prepare -Confirm:$false

        $global:OssmReleaseTestState.Release.isDraft | Should -BeTrue
        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter { $Arguments[1] -eq 'create' }
    }

    It 'pushes NuGet before creating a draft with generated notes, uploading assets, and publishing' {
        $global:OssmReleaseTestState.Release = $null
        Mock Invoke-CheckedCommand {
            $global:OssmReleaseTestState.Release | Should -BeNullOrEmpty
        } -ParameterFilter { $FileName -eq 'dotnet' -and $Arguments[1] -eq 'push' }

        & $publishScript -Tag v0.1.0 -Directory $directory -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter {
            $FileName -eq 'gh' -and $Arguments[1] -eq 'create' -and
            $Arguments -contains '--generate-notes' -and $Arguments -contains '--draft'
        }
        Should -Invoke Invoke-CheckedCommand -Times 7 -Exactly -ParameterFilter {
            $FileName -eq 'gh' -and $Arguments[1] -eq 'upload' -and $Arguments -notcontains '--clobber'
        }
        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter {
            $FileName -eq 'dotnet' -and $Arguments[1] -eq 'push'
        }
        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter {
            $FileName -eq 'gh' -and $Arguments[1] -eq 'edit' -and $Arguments -contains '--draft=false'
        }
    }

    It 'reuses matching assets and a signed NuGet package after partial publication' {
        $global:OssmReleaseTestState.Release.assets = @($names | ForEach-Object {
            [pscustomobject] @{
                name = $_
                digest = 'sha256:' + (Get-FileHash (Join-Path $directory $_)).Hash.ToLowerInvariant()
            }
        })
        $global:OssmReleaseTestState.ExistingPackage = Join-Path $TestDrive 'signed.nupkg'
        New-TestPackage $global:OssmReleaseTestState.ExistingPackage -Signed
        $global:OssmReleaseTestState.NuGetStatus = 200
        Remove-Item Env:NUGET_API_KEY

        & $publishScript -Tag v0.1.0 -Directory $directory -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter {
            $Arguments[1] -in @('create', 'upload', 'push')
        }
        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter {
            $FileName -eq 'gh' -and $Arguments[1] -eq 'edit'
        }
    }

    It 'uploads only missing assets in an existing draft' {
        $global:OssmReleaseTestState.Release.assets = @([pscustomobject] @{
            name = $names[0]
            digest = 'sha256:' + (Get-FileHash (Join-Path $directory $names[0])).Hash.ToLowerInvariant()
        })

        & $publishScript -Tag v0.1.0 -Directory $directory -Stage Upload -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 6 -Exactly -ParameterFilter { $Arguments[1] -eq 'upload' }
    }

    It 'refuses to replace a different existing asset' {
        $global:OssmReleaseTestState.Release.assets = @([pscustomobject] @{ name = $names[0]; digest = 'sha256:different' })

        { & $publishScript -Tag v0.1.0 -Directory $directory -Stage Upload -Confirm:$false } |
            Should -Throw '*Existing release asset differs*'
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -in @('upload', 'push', 'edit') }
    }

    It 'reuses a published release during preflight without requiring build artifacts' {
        $global:OssmReleaseTestState.Release.isDraft = $false
        $global:OssmReleaseTestState.Release.assets = @()

        & $publishScript -Tag v0.1.0 -Stage Prepare -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -eq 'create' }
    }

    It 'skips asset uploads to an already published release' {
        $global:OssmReleaseTestState.Release.isDraft = $false
        $global:OssmReleaseTestState.Release.assets = @()

        & $publishScript -Tag v0.1.0 -Directory $directory -Stage Upload -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -eq 'upload' }
    }

    It 'reuses complete published assets without modifying the release' {
        $global:OssmReleaseTestState.Release.isDraft = $false

        & $publishScript -Tag v0.1.0 -Directory $directory -Stage Upload -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -in @('upload', 'edit') }
    }

    It 'publishes NuGet without modifying an already-published release with no assets' {
        $global:OssmReleaseTestState.Release.isDraft = $false
        $global:OssmReleaseTestState.Release.assets = @()

        & $publishScript -Tag v0.1.0 -Directory $directory -Confirm:$false

        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter { $FileName -eq 'dotnet' -and $Arguments[1] -eq 'push' }
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -in @('upload', 'edit') }
    }

    It 'publishes NuGet independently of a <State> GitHub release' -ForEach @(
        @{ State = 'missing' }
        @{ State = 'draft' }
        @{ State = 'published' }
    ) {
        if ($State -eq 'missing') {
            $global:OssmReleaseTestState.Release = $null
        }
        else {
            $global:OssmReleaseTestState.Release.isDraft = $State -eq 'draft'
            $global:OssmReleaseTestState.Release.assets = @()
        }
        Mock Invoke-CheckedCommand { throw 'GitHub must not be required for NuGet publication' } -ParameterFilter { $FileName -eq 'gh' }

        & $publishScript -Tag v0.1.0 -Directory $directory -Stage NuGet -Confirm:$false

        Should -Invoke Invoke-CheckedCommand -Times 1 -Exactly -ParameterFilter { $FileName -eq 'dotnet' -and $Arguments[1] -eq 'push' }
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $FileName -eq 'gh' }
    }

    It 'leaves GitHub untouched when NuGet publication fails' {
        Mock Invoke-CheckedCommand { throw 'NuGet push failed' } -ParameterFilter { $FileName -eq 'dotnet' }

        { & $publishScript -Tag v0.1.0 -Directory $directory -Confirm:$false } |
            Should -Throw '*NuGet push failed*'

        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $FileName -eq 'gh' }
    }

    It 'refuses to skip a NuGet version with different payloads' {
        $global:OssmReleaseTestState.ExistingPackage = Join-Path $TestDrive 'different.nupkg'
        New-TestPackage $global:OssmReleaseTestState.ExistingPackage -Payload 'different' -Signed
        $global:OssmReleaseTestState.NuGetStatus = 200

        { & $publishScript -Tag v0.1.0 -Directory $directory -Stage NuGet -Confirm:$false } |
            Should -Throw '*already exists with different content*'
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $FileName -eq 'dotnet' }
    }

    It 'stops on NuGet service errors instead of attempting a push' {
        $global:OssmReleaseTestState.NuGetStatus = 503

        { & $publishScript -Tag v0.1.0 -Directory $directory -Stage NuGet -Confirm:$false } |
            Should -Throw '*HTTP 503*'
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $FileName -eq 'dotnet' }
    }

    It 'requires credentials only for a new NuGet package' {
        Remove-Item Env:NUGET_API_KEY

        { & $publishScript -Tag v0.1.0 -Directory $directory -Stage NuGet -Confirm:$false } |
            Should -Throw '*NUGET_API_KEY*'
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $FileName -eq 'dotnet' }
    }

    It 'does not treat GitHub lookup failures as a missing release' {
        Mock Invoke-CheckedCommand { throw 'GitHub unavailable' }

        { & $publishScript -Tag v0.1.0 -Directory $directory -Stage Prepare -Confirm:$false } |
            Should -Throw '*GitHub unavailable*'
        Should -Invoke Invoke-CheckedCommand -Times 0 -Exactly -ParameterFilter { $Arguments[1] -eq 'create' }
    }
}

Describe 'Release smoke helper' {
    It 'compiles with Add-Type without project implicit usings' {
        { Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseSmoke.cs') } | Should -Not -Throw
    }
}

AfterAll { Remove-Variable OssmReleaseTestState -Scope Global }
