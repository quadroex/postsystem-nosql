param(
    [ValidateSet('start', 'stop', 'verify', 'quick', 'benchmark', 'show')]
    [string]$Mode = 'start',
    [string]$Output = 'results'
)

$ErrorActionPreference = 'Stop'
$useWsl = -not (Get-Command docker -ErrorAction SilentlyContinue)
$keeperFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'results/wsl-keeper.txt'

function Get-Keeper {
    if (-not (Test-Path -LiteralPath $keeperFile)) { return }
    $record = Get-Content -LiteralPath $keeperFile
    if ($record.Count -ne 2 -or $record[0] -notmatch '^\d+$') { return }
    $process = Get-Process -Id ([int]$record[0]) -ErrorAction SilentlyContinue
    if ($process -and $process.Name -eq 'wsl' -and $process.StartTime.ToFileTimeUtc() -eq $record[1]) { $process }
}

function Invoke-Compose([string[]]$ComposeArgs) {
    if ($useWsl) {
        & wsl -d Ubuntu-24.04 -u root -- docker compose @ComposeArgs
    } else {
        & docker compose @ComposeArgs
    }
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed.' }
}

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    if ($Mode -eq 'stop') {
        Invoke-Compose -ComposeArgs @('stop')
        $keeper = Get-Keeper
        if ($keeper) { Stop-Process -InputObject $keeper }
        return
    }
    if ($useWsl) {
        if (-not (Get-Keeper)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $keeperFile) | Out-Null
            $keeper = Start-Process -FilePath wsl.exe -ArgumentList '-d Ubuntu-24.04 -- sleep infinity' -WindowStyle Hidden -PassThru
            @($keeper.Id, $keeper.StartTime.ToFileTimeUtc()) | Set-Content -LiteralPath $keeperFile
        }
        & wsl -d Ubuntu-24.04 -u root -- systemctl start docker
        if ($LASTEXITCODE -ne 0) { throw 'Could not start Docker in WSL.' }
    }
    Invoke-Compose -ComposeArgs @('up', '-d', '--wait')
    if ($Mode -eq 'start') { return }

    if (-not $env:POSTAL_PG) { $env:POSTAL_PG = 'Host=127.0.0.1;Port=55432;Database=postsystem_nosql;Username=postal;Password=postal_lab' }
    if (-not $env:POSTAL_MONGO) { $env:POSTAL_MONGO = 'mongodb://postal:postal_lab@127.0.0.1:57017/?authSource=admin&directConnection=true' }
    if (-not $env:POSTAL_MONGO_DB) { $env:POSTAL_MONGO_DB = 'postsystem_nosql' }

    if ($useWsl) {
        & dotnet build src/PostalTracking.csproj --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        $env:WSLENV = (@($env:WSLENV, 'POSTAL_PG/u', 'POSTAL_MONGO/u', 'POSTAL_MONGO_DB/u') | Where-Object { $_ }) -join ':'
        & wsl -d Ubuntu-24.04 -- dotnet src/bin/Release/net10.0/PostalTracking.dll $Mode $Output
    } else {
        & dotnet run --project src/PostalTracking.csproj --configuration Release -- $Mode $Output
    }
    if ($LASTEXITCODE -ne 0) { throw 'Lab command failed.' }
} finally {
    Pop-Location
}
