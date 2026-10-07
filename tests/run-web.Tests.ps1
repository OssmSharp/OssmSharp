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

    Context 'Standalone application logging' {
        BeforeEach {
            . $scriptPath -WhatIf
            Mock Start-Process {}
        }

        It 'sets console logging to warnings and above for child processes' {
            $environment = New-ProcessEnvironment @{}

            $environment['Logging__Console__LogLevel__Default'] | Should -Be 'Warning'
        }

        It 'enables separate file logs for <Application>' -ForEach @(
            @{ Application = 'OssmSharp.Web.Api' }
            @{ Application = 'OssmSharp.Web.AdminWeb' }
            @{ Application = 'OssmSharp.Web.GuestWeb' }
        ) {
            $project = "src/$Application/$Application.csproj"
            $expectedDirectory = Join-Path $script:RepositoryRoot "logs/$Application"

            Start-WebProcess 'test application' $project 'net10.0' @{}

            Should -Invoke Start-Process -Times 1 -Exactly -ParameterFilter {
                $Environment['OSSM__FileLogging__Directory'] -eq $expectedDirectory -and
                $Environment['Logging__Console__LogLevel__Default'] -eq 'Warning'
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
