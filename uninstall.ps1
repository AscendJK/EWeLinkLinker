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
Start-Sleep -Seconds 2

Write-Host "Removing service..."
sc.exe delete $serviceName | Out-Null

Write-Host "Service removed successfully!" -ForegroundColor Green
Write-Host "Note: Config files and logs were NOT deleted. You can manually remove the 'publish' directory if needed." -ForegroundColor Cyan
