[CmdletBinding()]
param(
    [ValidateSet("all", "contracts", "server", "web", "windows", "android")]
    [string]$Component = "all"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Check {
    param(
        [string]$Name,
        [scriptblock]$Command
    )

    Write-Host "`n==> $Name"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Test-Server {
    Invoke-Check "Server typecheck" { & npm.cmd --prefix "$repositoryRoot\server" run typecheck }
    Invoke-Check "Server tests" { & npm.cmd --prefix "$repositoryRoot\server" test }
    Invoke-Check "Server build" { & npm.cmd --prefix "$repositoryRoot\server" run build }
}

function Test-Contracts {
    Invoke-Check "Contract lint, examples, and generated models" {
        & npm.cmd --prefix "$repositoryRoot\contracts" run check
    }
}

function Test-Web {
    Invoke-Check "Web build" { & npm.cmd --prefix "$repositoryRoot\web" run build }
}

function Test-Windows {
    Invoke-Check "Windows tests" {
        & dotnet test "$repositoryRoot\windows\LiveQs.Windows.Tests\LiveQs.Windows.Tests.csproj"
    }
    Invoke-Check "Windows build" {
        & dotnet build "$repositoryRoot\windows\LiveQs.Windows\LiveQs.Windows.csproj" `
            -o "$repositoryRoot\windows\artifacts\verify"
    }
}

function Test-Android {
    Push-Location "$repositoryRoot\android"
    try {
        Invoke-Check "Android lint, tests, and debug build" { & .\gradlew.bat lint test assembleDebug }
    }
    finally {
        Pop-Location
    }
}

$components = if ($Component -eq "all") {
    @("contracts", "server", "web", "windows", "android")
}
else {
    @($Component)
}

foreach ($selectedComponent in $components) {
    switch ($selectedComponent) {
        "contracts" { Test-Contracts }
        "server" { Test-Server }
        "web" { Test-Web }
        "windows" { Test-Windows }
        "android" { Test-Android }
    }
}

Write-Host "`nAll requested checks passed."
