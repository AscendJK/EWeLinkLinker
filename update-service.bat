@echo off
chcp 65001 >nul
echo ========================================
echo     EWeLink Linker 服务更新脚本
echo ========================================

:: 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 请以管理员身份运行此脚本！
    echo 右键点击此文件，选择"以管理员身份运行"
    pause
    exit /b 1
)

set SERVICE_NAME=EWeLinkLinker
set PUBLISH_DIR=%~dp0publish\Service

echo.
echo [1/4] 停止服务...
net stop %SERVICE_NAME% 2>nul
timeout /t 2 /nobreak >nul

echo [2/4] 删除旧服务...
sc.exe delete %SERVICE_NAME% 2>nul
timeout /t 2 /nobreak >nul

echo [3/4] 安装新服务...
sc.exe create %SERVICE_NAME% binPath= "%PUBLISH_DIR%\EWeLinkLinker.Service.exe" start= auto DisplayName= "EWeLink Linker Service"
sc.exe description %SERVICE_NAME% "Automatically controls eWeLink devices based on PC power events"

echo [4/4] 启动服务...
net start %SERVICE_NAME%

echo.
echo ========================================
echo     更新完成！
echo ========================================
echo 服务状态:
sc.exe query %SERVICE_NAME% | findstr "STATE"
echo.
pause
