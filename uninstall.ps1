# Uninstall EWeLink Linker Service
# Run as Administrator

$serviceName = "EWeLinkLinker"

# Check admin privileges
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "ERROR: 请以管理员身份运行此脚本！" -ForegroundColor Red
    exit 1
}

# Check if service exists
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$serviceName' not found. Nothing to uninstall." -ForegroundColor Yellow
    exit 0
}

# Confirm
$result = Read-Host "确认卸载 EWeLink Linker Service？(Y/N)"
if ($result -ne "Y" -and $result -ne "y") {
    Write-Host "Cancelled." -ForegroundColor Yellow
    exit 0
}

Write-Host "Stopping service..."
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
    Write-Host "ERROR: 服务停不下来（当前状态 $($cur.Status)），已中止，没有删除服务" -ForegroundColor Red
    exit 1
}

Write-Host "Removing service..."
# sc.exe 的退出码才是结论：以前这里把输出丢掉、无条件打印"删除成功"，
# 1072（已标记删除但句柄还开着）的时候服务其实还挂在 SCM 里
$deleteOutput = sc.exe delete $serviceName 2>&1
$deleteExit = $LASTEXITCODE
if ($deleteExit -ne 0) {
    Write-Host "ERROR: sc.exe delete 退出码 $deleteExit（1072=已标记删除但句柄还开着，先关掉 ConfigApp/服务进程再重试）" -ForegroundColor Red
    Write-Host "       输出：$deleteOutput" -ForegroundColor Red
    exit 1
}

# 删除是异步的：退出码 0 也可能只是"已标记"。等到 SCM 真读不到为止再报成功
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 300
}
$left = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($left) {
    Write-Host "ERROR: sc.exe delete 返回 0，但服务还在 SCM 里（状态 $($left.Status)）⇒ 还没真删掉，关掉占用进程后重跑，别当成已卸载" -ForegroundColor Red
    exit 1
}

Write-Host "Service removed (SCM 已查不到 '$serviceName')." -ForegroundColor Green
Write-Host "Note: Config files and logs were NOT deleted. You can manually remove the 'publish' directory if needed." -ForegroundColor Cyan
