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

# Stop and remove existing —— 和 install.ps1 同一套：每一步看真结论，不吞异常
# 原来整段包在 try{}catch{} 里：停不下来、删不掉都当没事继续往下装，最后报"安装完成"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue

    # 等到真停下来再删：固定 Sleep 2 会在动作还在飞的时候就把服务删掉
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $cur = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if (-not $cur -or $cur.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 300
    }
    $cur = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($cur -and $cur.Status -ne 'Stopped') {
        Write-Host "ERROR: 旧服务停不下来（当前状态 $($cur.Status)），已中止，没有删服务、没有覆盖文件" -ForegroundColor Red
        pause
        exit 1
    }

    sc.exe delete $serviceName | Out-Null
    $deleteExit = $LASTEXITCODE
    if ($deleteExit -ne 0) {
        Write-Host "ERROR: sc.exe delete 退出码 $deleteExit（1072=已标记删除但句柄还开着，先关掉 ConfigApp/服务进程再重试）" -ForegroundColor Red
        pause
        exit 1
    }

    # 删除是异步的：New-Service 撞上"标记删除"会报 1056，所以要等它真从 SCM 里消失
    $deadline = (Get-Date).AddSeconds(20)
    while (@(Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Write-Host "ERROR: 服务还没从 SCM 里消失，继续装会报 1056。请关掉 ConfigApp/服务进程后重跑。" -ForegroundColor Red
        pause
        exit 1
    }
    Write-Host "Existing service removed." -ForegroundColor Green
}

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
