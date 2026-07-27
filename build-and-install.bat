@echo off
chcp 65001 >nul
echo ========================================
echo     EWeLink Linker Build + Install
echo ========================================

cd /d "%~dp0"

:: Request admin privileges
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Requesting admin privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo [1/4] Cleaning...
if exist "publish\ConfigApp" rmdir /s /q "publish\ConfigApp"
if exist "publish\Service" rmdir /s /q "publish\Service"

echo.
echo [2/4] Building ConfigApp...
dotnet publish "src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj" -c Release -o "publish\ConfigApp" --self-contained false
if %errorlevel% neq 0 (
    echo [ERROR] ConfigApp build failed!
    pause
    exit /b 1
)

echo.
echo [3/4] Building Service...
dotnet publish "src\EWeLinkLinker.Service\EWeLinkLinker.Service.csproj" -c Release -o "publish\Service" --self-contained false
if %errorlevel% neq 0 (
    echo [ERROR] Service build failed!
    pause
    exit /b 1
)

echo.
echo [4/4] Installing Service...

:: Stop and delete old service
net stop EWeLinkLinker 2>nul
timeout /t 2 /nobreak >nul
sc.exe delete EWeLinkLinker 2>nul
timeout /t 2 /nobreak >nul

:: Create new service
sc.exe create EWeLinkLinker binPath= "%CD%\publish\Service\EWeLinkLinker.Service.exe" start= auto DisplayName= "EWeLink Linker Service"
sc.exe description EWeLinkLinker "Automatically controls eWeLink devices based on PC power events"

:: Start service
net start EWeLinkLinker

echo.
echo ========================================
echo     Installation Complete!
echo ========================================
echo.
echo Service status:
sc.exe query EWeLinkLinker | findstr "STATE"
echo.
pause
