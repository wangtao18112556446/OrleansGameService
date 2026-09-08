$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if (-not $docker) {
        throw 'Docker Desktop or Docker Engine is required to run the integration script. Install Docker and ensure docker is on PATH, then rerun: pwsh -File scripts/Test-Integration.ps1'
    }

    $composeVersion = & docker compose version 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($composeVersion)) {
        throw 'Docker Compose is required to run the integration script. Install Docker Desktop (with Compose enabled) or enable the Docker Compose plugin, then rerun: pwsh -File scripts/Test-Integration.ps1'
    }

    # 使用独立 Compose 项目和端口，避免测试重启正在开发的游戏实例。
    $env:GAME_POSTGRES_PORT = '15432'
    $env:GAME_REDIS_PORT = '16379'
    $env:GAME_GATEWAY_PORT = '18080'
    $env:GameTests__Postgres = 'Host=localhost;Port=15432;Database=orleans_game;Username=game;Password=game'
    $env:GameTests__Gateway = 'http://localhost:18080'
    $env:GameTests__StateFile = Join-Path $repo 'TestResults/smoke-state.json'
    dotnet build OrleansGameService.slnx
    docker compose -p orleans-game-tests up --build -d

    function Wait-Gateway {
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $response = Invoke-WebRequest "$env:GameTests__Gateway/health/ready" -TimeoutSec 3
                if ($response.StatusCode -eq 200) { return }
            } catch { }
            Start-Sleep -Seconds 2
        }
        throw 'Gateway did not become ready.'
    }

    Wait-Gateway
    dotnet test OrleansGameService.slnx --no-build --filter FullyQualifiedName~PostgresPersistenceTests --logger 'trx;LogFileName=postgres.trx'
    $env:GameTests__Phase = 'prepare'
    dotnet test OrleansGameService.slnx --no-build --filter FullyQualifiedName~GatewaySmokeTests --logger 'trx;LogFileName=smoke-prepare.trx'
    docker compose -p orleans-game-tests restart gateway
    Wait-Gateway
    $env:GameTests__Phase = 'verify'
    dotnet test OrleansGameService.slnx --no-build --filter FullyQualifiedName~GatewaySmokeTests --logger 'trx;LogFileName=smoke-restart.trx'
    # 重跑初始化服务，覆盖已有 Orleans 表及业务迁移记录的重复启动路径。
    docker compose -p orleans-game-tests run --rm orleans-schema
    $cleanupQuery = docker compose -p orleans-game-tests exec -T postgres psql -U game -d orleans_game -tAc "SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'CleanupDefunctSiloEntriesKey'"
    if ($LASTEXITCODE -ne 0 -or ($cleanupQuery -join "`n") -notmatch 'Status\s*=\s*6') {
        throw 'Orleans schema did not contain a valid CleanupDefunctSiloEntriesKey query after initialization.'
    }
} finally {
    Pop-Location
}
