# Install EWeLink Linker Service
# Can be run from ConfigApp's publish directory or project root

param(
    [switch]$NoBuild = $false
)

$ErrorActionPreference = "Stop"
$serviceName = "EWeLinkLinker"
$displayName = "EWeLink Linker Service"
$description = "Automatically controls eWeLink devices based on PC power events"

# 先查权限，再开 transcript。
# 以前顺序反了：这个脚本也支持从 publish\ConfigApp\ 里跑，而那两个二进制目录现在的
# ACL 只允许管理员写，非提权会话在 Start-Transcript 就"访问被拒"当场中断
# （$ErrorActionPreference='Stop'），那句"请以管理员身份运行"永远看不到。
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: 请以管理员身份运行此脚本！" -ForegroundColor Red
    Write-Host "Right-click the program and select 'Run as administrator'" -ForegroundColor Yellow
    exit 1
}

# Start transcript logging（写到 TEMP，免得又踩目录权限）
$logFile = Join-Path $env:TEMP ("EWeLinkLinker_install_" + (Get-Date).ToString("yyyyMMdd_HHmmss") + ".log")
Start-Transcript -Path $logFile -Force
Write-Host "Transcript: $logFile" -ForegroundColor Cyan

# Detect project root
$scriptDir = $PSScriptRoot
$projectRoot = $scriptDir

# If running from publish/ConfigApp/, go up to find project root
if ($scriptDir -match "publish\\ConfigApp$") {
    $projectRoot = Split-Path (Split-Path $scriptDir) -Parent
}
elseif ($scriptDir -match "publish\\Service$") {
    $projectRoot = Split-Path $scriptDir -Parent
}

$serviceProject = Join-Path $projectRoot "src\EWeLinkLinker.Service\EWeLinkLinker.Service.csproj"
$publishDir = Join-Path $projectRoot "publish\Service"
$exePath = Join-Path $publishDir "EWeLinkLinker.Service.exe"

Write-Host "Script directory: $scriptDir" -ForegroundColor Cyan
Write-Host "Project root: $projectRoot" -ForegroundColor Cyan

# Verify project exists
if (-not (Test-Path $serviceProject)) {
    Write-Host "ERROR: Service project not found at: $serviceProject" -ForegroundColor Red
    Write-Host "Please run this script from the project root or publish/ConfigApp directory." -ForegroundColor Yellow
    exit 1
}

# Build and publish (unless -NoBuild specified)
# 一次发两个：以前这里只 publish Service，ConfigApp 是另一条独立动作，
# 于是盘上出现过 Service=一个 commit、ConfigApp=另一个 commit 的混版（两边共用 Core.dll 却版本不一致）。
if (-not $NoBuild) {
    $configAppProject = Join-Path $projectRoot "src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj"
    Write-Host "Building Service + ConfigApp..." -ForegroundColor Cyan
    Push-Location $projectRoot

    dotnet publish $serviceProject -c Release -o publish/Service --self-contained false
    $serviceBuildExit = $LASTEXITCODE

    dotnet publish $configAppProject -c Release -o publish/ConfigApp --self-contained false
    $configAppBuildExit = $LASTEXITCODE

    Pop-Location

    if ($serviceBuildExit -ne 0 -or $configAppBuildExit -ne 0) {
        Write-Host "ERROR: build failed (Service=$serviceBuildExit ConfigApp=$configAppBuildExit)" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded (both projects)." -ForegroundColor Green
}

# Verify exe exists
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Service executable not found at: $exePath" -ForegroundColor Red
    Write-Host "Build may have failed or output directory is wrong." -ForegroundColor Yellow
    exit 1
}

# Create shared config directory (both ConfigApp and Service resolve publish\config via ..\config)
$configDir = Join-Path $projectRoot "publish\config"
$logDir = Join-Path $publishDir "logs"

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Shared config file is created by ConfigApp (publish\config\linker.json); service reads the same file directly
$sharedConfig = Join-Path $configDir "linker.json"
if (-not (Test-Path $sharedConfig)) {
    Write-Host "WARNING: No config found at $sharedConfig. Please run ConfigApp first to create linker.json" -ForegroundColor Yellow
}

# Remove existing service if present
$hadService = $false
$wasStartup = $null
try {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        $hadService = $true
        $wasStartup = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartMode
        Write-Host "Removing existing service... (startup was: $wasStartup)"
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        # 等到真停下再删：固定 Sleep 2 在动作还在飞的时候就把服务删了
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        sc.exe delete $serviceName | Out-Null
        $delExit = $LASTEXITCODE
        if ($delExit -ne 0) { Write-Host "ERROR: sc.exe delete 退出码 $delExit（1072=已标记删除但句柄还开着，先关掉占用进程再重试）" -ForegroundColor Red; Stop-Transcript; exit 1 }
        # 删除是异步的：New-Service 撞上"标记删除"会直接失败，所以要等它真的消失
        $deadline = (Get-Date).AddSeconds(20)
        while (@(Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            Write-Host "ERROR: 服务还没从 SCM 里消失，继续装会报 1056。请关掉 ConfigApp/服务进程后重跑。" -ForegroundColor Red
            Stop-Transcript
            exit 1
        }
        Write-Host "Existing service removed." -ForegroundColor Green
    }
}
catch {
    Write-Host "ERROR: Could not remove existing service: $_" -ForegroundColor Red
    Write-Host "注意：旧服务可能已经被删掉但新服务还没建，装完之前自动化不会运行。" -ForegroundColor Yellow
    Stop-Transcript
    exit 1
}

# Install service (runs as LocalSystem)
Write-Host "Installing service..." -ForegroundColor Cyan
try {
    New-Service -Name $serviceName `
        -DisplayName $displayName `
        -Description $description `
        -BinaryPathName $exePath `
        -StartupType Automatic `
        -ErrorAction Stop

    Write-Host "Service installed successfully!" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Failed to install service: $_" -ForegroundColor Red
    if ($hadService) {
        Write-Host "原来的服务已经被删掉了（之前的启动类型：$wasStartup）。" -ForegroundColor Yellow
        Write-Host "现在这台机器上没有 EWeLinkLinker 服务，规则不会自动执行；修好上面这条错误后重跑本脚本。" -ForegroundColor Yellow
    }
    Stop-Transcript
    exit 1
}

# Start service
try {
    Start-Service -Name $serviceName -ErrorAction Stop
    Write-Host "Service started." -ForegroundColor Green
}
catch {
    Write-Host "Warning: Could not start service: $_" -ForegroundColor Yellow
    Write-Host "Try starting it manually: Start-Service $serviceName" -ForegroundColor Cyan
}

Write-Host "" -ForegroundColor Green
Write-Host "=== Installation Complete ===" -ForegroundColor Green
Write-Host "Config: $sharedConfig" -ForegroundColor Cyan
Write-Host "Logs:   $logDir" -ForegroundColor Cyan
Write-Host "Service: $serviceName" -ForegroundColor Cyan

Stop-Transcript
