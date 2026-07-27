@echo off
chcp 65001 >nul
echo ========================================
echo     EWeLink Linker Build Script
echo ========================================

cd /d "%~dp0"

echo.
echo [1/3] Cleaning...
if exist "publish\ConfigApp" rmdir /s /q "publish\ConfigApp"
if exist "publish\Service" rmdir /s /q "publish\Service"

echo.
echo [2/3] Building ConfigApp...
dotnet publish "src\EWeLinkLinker.ConfigApp\EWeLinkLinker.ConfigApp.csproj" -c Release -o "publish\ConfigApp" --self-contained false
if %errorlevel% neq 0 (
    echo [ERROR] ConfigApp build failed!
    pause
    exit /b 1
)

echo.
echo [3/3] Building Service...
dotnet publish "src\EWeLinkLinker.Service\EWeLinkLinker.Service.csproj" -c Release -o "publish\Service" --self-contained false
if %errorlevel% neq 0 (
    echo [ERROR] Service build failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo     Build Complete!
echo ========================================
echo.
echo Next steps:
echo   1. Open ConfigApp
echo   2. Click "Uninstall Service"
echo   3. Click "Install Service"
echo.
pause
