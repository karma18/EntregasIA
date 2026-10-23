$ErrorActionPreference = 'Stop'

function Write-HookJson([object]$payload) {
    [Console]::Out.WriteLine(($payload | ConvertTo-Json -Compress -Depth 6))
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-HookJson @{}
        exit 0
    }

    $inputObject = $raw | ConvertFrom-Json
    $filePath = [string]$inputObject.file_path
    if ([string]::IsNullOrWhiteSpace($filePath)) {
        Write-HookJson @{}
        exit 0
    }

    $normalized = $filePath.Replace('\', '/')
    $isApiOrTests = $normalized -match '/src/Talleres\.Api/' -or
        $normalized -match '/tests/Talleres\.Api\.Tests/' -or
        $normalized -match '/src/Talleres\.Api/[^/]+\.(cs|csproj)$' -or
        $normalized -match '/tests/Talleres\.Api\.Tests/[^/]+\.(cs|csproj)$'

    $isSource = $normalized -match '\.(cs|csproj)$'
    if (-not ($isApiOrTests -and $isSource)) {
        Write-HookJson @{}
        exit 0
    }

    $stamp = Join-Path $PSScriptRoot '.pending-api-tests'
    Set-Content -Path $stamp -Value $filePath -Encoding utf8
    Write-HookJson @{}
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    Write-HookJson @{}
    exit 0
}
