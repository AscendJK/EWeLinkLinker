# Simple install script - no build, just install
param(
    [string]$ServicePath = "E:\ClaudeCode\EWeLinkLinker\publish\Service\EWeLinkLinker.Service.exe"
)

$serviceName = "EWeLinkLinker"

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: 请以管理员身份运行！" -ForegroundColor Red
    pause
    exit 1
}

# Stop and remove existing
try {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Stopping existing service..."
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
    }
} catch { }

# Install
if (-not (Test-Path $ServicePath)) {
    Write-Host "ERROR: 服务程序不存在: $ServicePath" -ForegroundColor Red
    Write-Host "请先跑 install.ps1（会构建 publish\Service），或用 -ServicePath 指到真实存在的那个 exe。" -ForegroundColor Yellow
    pause
    exit 1
}
Write-Host "Installing service..."
try {
    New-Service -Name $serviceName `
        -DisplayName "EWeLink Linker Service" `
        -Description "Automatically controls eWeLink devices based on PC power events" `
        -BinaryPathName (Resolve-Path $ServicePath).Path `
        -StartupType Automatic `
        -ErrorAction Stop

    Write-Host "Service installed!" -ForegroundColor Green

    # Start，然后按真状态说话：以前这里 -ErrorAction SilentlyContinue 之后无条件打印
    # "Service started."，服务其实没起来也照样说起来了
    Start-Service -Name $serviceName -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(15)
    $st = (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
    while ($st -ne 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $st = (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
    }
    if ($st -eq 'Running') { Write-Host "Service started. (status=Running)" -ForegroundColor Green }
    else { Write-Host "WARNING: 服务没有进入 Running，当前状态=$st，请看服务日志" -ForegroundColor Yellow }
} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
}

pause
