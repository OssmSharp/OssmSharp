BeforeAll {
    $scriptPath = Join-Path $PSScriptRoot '../run-web.ps1'

    function Invoke-TestDotNet {
        param(
            [Parameter(ValueFromRemainingArguments)]
            [string[]] $Arguments
        )
    }
}

Describe 'run-web.ps1' {
    It 'supports a side-effect-free preview with its documented defaults' {
        { & $scriptPath -WhatIf } | Should -Not -Throw
    }

    It 'rejects duplicate listener ports before starting applications' {
        { & $scriptPath -AdminPort 5001 -GuestPort 5001 -ApiPort 5003 -WhatIf } |
            Should -Throw -ExpectedMessage '*must use different ports*'
    }

    It 'rejects an unsupported verbosity before starting applications' {
        { & $scriptPath -Verbosity unsupported -WhatIf } | Should -Throw
    }

    It 'uses the first dotnet on PATH when several are installed' {
        $first = (Get-Command dotnet -CommandType Application -TotalCount 1).Path
        $shadow = Join-Path $TestDrive 'shadow'
        New-Item -ItemType Directory -Path $shadow | Out-Null
        $stub = Join-Path $shadow ($IsWindows ? 'dotnet.exe' : 'dotnet')
        New-Item -ItemType File -Path $stub | Out-Null
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode($stub, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute')
        }
        $originalPath = $env:PATH
        try {
            $env:PATH = $env:PATH + [IO.Path]::PathSeparator + $shadow
            . $scriptPath -WhatIf

            $script:DotNet | Should -BeOfType [string]
            $script:DotNet | Should -Be $first
        }
        finally {
            $env:PATH = $originalPath
        }
    }

    Context 'Combined host configuration' {
        BeforeEach {
            . $scriptPath -WhatIf
            Mock Start-Process {}
        }

        It 'sets console logging to warnings and above for child processes' {
            $environment = New-ProcessEnvironment @{}

            $environment['Logging__Console__LogLevel__Default'] | Should -Be 'Warning'
        }

        It 'sets the repository log root for the host and its applications' {
            $project = 'src/OssmSharp.Web/OssmSharp.Web.csproj'
            $expectedDirectory = Join-Path $script:RepositoryRoot 'logs'

            Start-WebProcess 'web host' $project $nativeFramework @{}

            Should -Invoke Start-Process -Times 1 -Exactly -ParameterFilter {
                $Environment['OSSM__FileLogging__Directory'] -eq $expectedDirectory -and
                $Environment['Logging__Console__LogLevel__Default'] -eq 'Warning'
            }
        }

        It 'passes the combined host ports and device selection without a launcher service key' {
            Start-WebProcess 'web host' 'src/OssmSharp.Web/OssmSharp.Web.csproj' $nativeFramework @{
                'OSSM__Web__ApiPort' = '5103'
                'OSSM__Web__GuestPort' = '5101'
                'OSSM__Web__AdminPort' = '5102'
                'OSSM__Control__DeviceIdentifier' = 'test-device'
            }

            Should -Invoke Start-Process -Times 1 -Exactly -ParameterFilter {
                $Environment['OSSM__Web__ApiPort'] -eq '5103' -and
                $Environment['OSSM__Web__GuestPort'] -eq '5101' -and
                $Environment['OSSM__Web__AdminPort'] -eq '5102' -and
                $Environment['OSSM__Control__DeviceIdentifier'] -eq 'test-device' -and
                -not $Environment.ContainsKey('OSSM__Control__AdminWebServiceKey') -and
                -not $Environment.ContainsKey('OSSM__ControlClient__ServiceKey')
            }
        }
    }

    Context 'Verbosity forwarding' {
        BeforeEach {
            . $scriptPath -WhatIf
            $script:DotNet = 'Invoke-TestDotNet'
            $LASTEXITCODE = 0
            Mock Invoke-TestDotNet {}
            Mock Start-Process {}
        }

        It 'defaults build and run commands to quiet verbosity' {
            Invoke-DotNet @('build', 'example.csproj')
            Start-WebProcess 'test application' 'example.csproj' 'net10.0' @{}

            Should -Invoke Invoke-TestDotNet -Times 1 -Exactly -ParameterFilter {
                ($Arguments -join '|') -eq 'build|example.csproj|--verbosity|quiet'
            }
            Should -Invoke Start-Process -Times 1 -Exactly -ParameterFilter {
                ($ArgumentList -join '|') -eq 'run|--project|example.csproj|--framework|net10.0|--no-build|--no-restore|--no-launch-profile|--verbosity|quiet'
            }
        }

        It 'forwards <Value> verbosity to build and run commands' -ForEach @(
            @{ Value = 'quiet' }
            @{ Value = 'minimal' }
            @{ Value = 'normal' }
            @{ Value = 'detailed' }
            @{ Value = 'diagnostic' }
        ) {
            . $scriptPath -WhatIf -Verbosity $Value
            $script:DotNet = 'Invoke-TestDotNet'

            Invoke-DotNet @('build', 'example.csproj')
            Start-WebProcess 'test application' 'example.csproj' 'net10.0' @{}

            Should -Invoke Invoke-TestDotNet -Times 1 -Exactly -ParameterFilter {
                ($Arguments -join '|') -eq "build|example.csproj|--verbosity|$Value"
            }
            Should -Invoke Start-Process -Times 1 -Exactly -ParameterFilter {
                ($ArgumentList -join '|') -eq "run|--project|example.csproj|--framework|net10.0|--no-build|--no-restore|--no-launch-profile|--verbosity|$Value"
            }
        }
    }
}
