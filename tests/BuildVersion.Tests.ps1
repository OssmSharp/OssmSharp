BeforeAll {
    $repositoryRoot = Split-Path $PSScriptRoot

    function Invoke-FixtureCommand {
        param([string] $Command, [string[]] $Arguments)

        $result = & $Command @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "$Command failed ($LASTEXITCODE): $($result -join [Environment]::NewLine)"
        }
        return $result
    }

    function Get-FixtureVersion {
        Invoke-FixtureCommand dotnet @('build', $project, '-c', 'Release', '--verbosity', 'quiet',
            '-bl:.artifacts/version-{}.binlog') | Out-Null
        $assembly = Join-Path $fixture '.artifacts/bin/Fixture/release/Fixture.dll'
        return [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    }
}

Describe 'Shared binary version stamps' {
    BeforeEach {
        $fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $fixture | Out-Null
        Copy-Item (Join-Path $repositoryRoot 'Directory.Build.props') $fixture
        Copy-Item (Join-Path $repositoryRoot 'Directory.Build.targets') $fixture
        $project = Join-Path $fixture 'Fixture.csproj'
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Remove="StyleCop.Analyzers" />
  </ItemGroup>
</Project>
'@ | Set-Content $project
        '.artifacts/' | Set-Content (Join-Path $fixture '.gitignore')
        'original' | Set-Content (Join-Path $fixture 'tracked.txt')
        Push-Location $fixture
        Invoke-FixtureCommand git @('init', '--quiet') | Out-Null
        Invoke-FixtureCommand git @('config', 'commit.gpgsign', 'false') | Out-Null
        Invoke-FixtureCommand git @('add', '.') | Out-Null
        Invoke-FixtureCommand git @('-c', 'user.name=Version Test', '-c', 'user.email=version@example.invalid',
            'commit', '--quiet', '-m', 'Initial fixture') | Out-Null
        $commit = (Invoke-FixtureCommand git @('rev-parse', 'HEAD')).Trim()
        $expected = "0.1.0+$($commit.Substring(0, 7))"
    }

    AfterEach {
        Pop-Location
    }

    It 'stamps a clean build with one short commit and numeric file version' {
        $version = Get-FixtureVersion

        $version.ProductVersion | Should -BeExactly $expected
        $version.FileVersion | Should -BeExactly '0.1.0.0'
        (Invoke-FixtureCommand git @('status', '--porcelain')).Count | Should -Be 0
    }

    It 'adds -private for <Kind> changes' -ForEach @(
        @{ Kind = 'unstaged' }
        @{ Kind = 'staged' }
        @{ Kind = 'untracked' }
    ) {
        if ($Kind -eq 'untracked') {
            'new file' | Set-Content (Join-Path $fixture 'new.txt')
        }
        else {
            'changed' | Set-Content (Join-Path $fixture 'tracked.txt')
            if ($Kind -eq 'staged') {
                Invoke-FixtureCommand git @('add', 'tracked.txt') | Out-Null
            }
        }

        (Get-FixtureVersion).ProductVersion | Should -BeExactly "$expected-private"
    }

    It 'updates clean-to-dirty-to-clean stamps without cleaning output' {
        (Get-FixtureVersion).ProductVersion | Should -BeExactly $expected
        $untracked = Join-Path $fixture 'new.txt'
        'new file' | Set-Content $untracked
        (Get-FixtureVersion).ProductVersion | Should -BeExactly "$expected-private"
        Remove-Item -LiteralPath $untracked
        (Get-FixtureVersion).ProductVersion | Should -BeExactly $expected
    }

    It 'updates the commit stamp without cleaning output' {
        (Get-FixtureVersion).ProductVersion | Should -BeExactly $expected
        'changed' | Set-Content (Join-Path $fixture 'tracked.txt')
        Invoke-FixtureCommand git @('add', 'tracked.txt') | Out-Null
        Invoke-FixtureCommand git @('-c', 'user.name=Version Test', '-c', 'user.email=version@example.invalid',
            'commit', '--quiet', '-m', 'Changed fixture') | Out-Null
        $nextCommit = (Invoke-FixtureCommand git @('rev-parse', 'HEAD')).Trim()

        (Get-FixtureVersion).ProductVersion | Should -BeExactly "0.1.0+$($nextCommit.Substring(0, 7))"
    }

    It 'stamps a detached HEAD normally' {
        Invoke-FixtureCommand git @('switch', '--detach', '--quiet', 'HEAD') | Out-Null

        (Get-FixtureVersion).ProductVersion | Should -BeExactly $expected
    }

    It 'fails explicitly when Git metadata is missing' {
        Remove-Item -LiteralPath (Join-Path $fixture '.git') -Recurse -Force

        { Get-FixtureVersion } | Should -Throw '*source revision*'
    }
}
