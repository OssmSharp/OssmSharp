BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleaseTools.psm1') -Force
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

Describe 'Release smoke helper' {
    It 'compiles with Add-Type without project implicit usings' {
        { Add-Type -Path (Join-Path $PSScriptRoot 'ReleaseSmoke.cs') } | Should -Not -Throw
    }
}
