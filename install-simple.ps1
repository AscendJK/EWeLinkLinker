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
Write-Host "Installing service..."
try {
    New-Service -Name $serviceName `
        -DisplayName "EWeLink Linker Service" `
        -Description "Automatically controls eWeLink devices based on PC power events" `
        -BinaryPathName $ServicePath `
        -StartupType Automatic `
        -ErrorAction Stop

    Write-Host "Service installed!" -ForegroundColor Green

    # Start
    Start-Service -Name $serviceName -ErrorAction SilentlyContinue
    Write-Host "Service started." -ForegroundColor Green
} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
}

pause
