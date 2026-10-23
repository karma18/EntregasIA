$ErrorActionPreference = 'Continue'

function Write-HookJson([object]$payload) {
    [Console]::Out.WriteLine(($payload | ConvertTo-Json -Compress -Depth 6))
}

function Get-RepoRoot {
    if ($PSScriptRoot) {
        return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    }
    return (Get-Location).Path
}

try {
    $raw = [Console]::In.ReadToEnd()
    $status = 'completed'
    $loopCount = 0
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $inputObject = $raw | ConvertFrom-Json
        if ($inputObject.status) { $status = [string]$inputObject.status }
        if ($null -ne $inputObject.loop_count) { $loopCount = [int]$inputObject.loop_count }
    }

    if ($status -ne 'completed') {
        Write-HookJson @{}
        exit 0
    }

    $stamp = Join-Path $PSScriptRoot '.pending-api-tests'
    if (-not (Test-Path -LiteralPath $stamp)) {
        Write-HookJson @{}
        exit 0
    }

    Remove-Item -LiteralPath $stamp -Force -ErrorAction SilentlyContinue

    $lock = Join-Path $env:TEMP 'entregasia-api-tests.lock'
    if (Test-Path -LiteralPath $lock) {
        Write-HookJson @{
            followup_message = 'El hook de pruebas no volvio a ejecutar la suite porque ya hay una corrida de dotnet test en este checkout. Espera a que termine e integra despues.'
        }
        exit 0
    }

    New-Item -ItemType File -Path $lock -Force | Out-Null
    $root = Get-RepoRoot
    $output = ''
    $exitCode = 1
    try {
        $output = & dotnet test (Join-Path $root 'CursoCursor.slnx') --nologo 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    }

    $summary = ($output -split "`r?`n" | Select-Object -Last 20) -join "`n"
    if ($summary.Length -gt 3500) {
        $summary = $summary.Substring($summary.Length - 3500)
    }

    if ($exitCode -eq 0) {
        Write-HookJson @{}
        exit 0
    }

    $followup = @"
Las pruebas unitarias de la API fallaron (dotnet test CursoCursor.slnx, codigo $exitCode). Corrige el codigo o las pruebas sin debilitar expectativas. No vuelvas a pedir commit.

Resumen:
$summary
"@

    if ($loopCount -ge 2) {
        Write-HookJson @{}
        exit 0
    }

    Write-HookJson @{ followup_message = $followup }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    Write-HookJson @{}
    exit 0
}
