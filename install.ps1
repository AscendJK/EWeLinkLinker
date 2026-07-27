# Install EWeLink Linker Service
# Can be run from ConfigApp's publish directory or project root

param(
    [switch]$NoBuild = $false
)

$ErrorActionPreference = "Stop"
$serviceName = "EWeLinkLinker"
$displayName = "EWeLink Linker Service"
$description = "Automatically controls eWeLink devices based on PC power events"

# Start transcript logging
$logFile = Join-Path $PSScriptRoot "install.log"
Start-Transcript -Path $logFile -Force

# Check admin privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: 请以管理员身份运行此脚本！" -ForegroundColor Red
    Write-Host "Right-click the program and select 'Run as administrator'" -ForegroundColor Yellow
    Stop-Transcript
    exit 1
}

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
if (-not $NoBuild) {
    Write-Host "Building Service project..." -ForegroundColor Cyan
    Push-Location $projectRoot
    dotnet publish $serviceProject -c Release -o publish/Service --self-contained false
    $buildExitCode = $LASTEXITCODE
    Pop-Location

    if ($buildExitCode -ne 0) {
        Write-Host "ERROR: Build failed with exit code $buildExitCode" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

# Verify exe exists
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Service executable not found at: $exePath" -ForegroundColor Red
    Write-Host "Build may have failed or output directory is wrong." -ForegroundColor Yellow
    exit 1
}

# Create config and logs directories
$configDir = Join-Path $publishDir "config"
$logDir = Join-Path $publishDir "logs"

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Copy config from ConfigApp if exists
$appConfig = Join-Path $projectRoot "publish\ConfigApp\config\linker.json"
$svcConfig = Join-Path $configDir "linker.json"

if (Test-Path $appConfig) {
    Copy-Item $appConfig $svcConfig -Force
    Write-Host "Copied config from ConfigApp to Service directory" -ForegroundColor Green
}
elseif (-not (Test-Path $svcConfig)) {
    Write-Host "WARNING: No config found. Please run ConfigApp first to create linker.json" -ForegroundColor Yellow
}

# Remove existing service if present
try {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Removing existing service..."
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 3
        Write-Host "Existing service removed." -ForegroundColor Green
    }
}
catch {
    Write-Host "Warning: Could not remove existing service: $_" -ForegroundColor Yellow
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
Write-Host "Config: $svcConfig" -ForegroundColor Cyan
Write-Host "Logs:   $logDir" -ForegroundColor Cyan
Write-Host "Service: $serviceName" -ForegroundColor Cyan

Stop-Transcript
