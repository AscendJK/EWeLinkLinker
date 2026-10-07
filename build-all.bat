@echo off
chcp 65001 >nul
echo ========================================
echo     EWeLink Linker Build Script
echo ========================================

cd /d "%~dp0"

echo.
echo [1/3] Skipping destructive clean on purpose...
REM 这里以前是 rmdir /s /q publish\ConfigApp 和 publish\Service，在构建**之前**执行，
REM 既不停服务也不看结果。两个后果：构建一失败，机器上就没有可跑的产物，而服务的
REM binPath 正指着被删空的目录，下次开机起不来；另外 publish\Service\logs 里他 7 天的
REM 日志会被一起删掉。dotnet publish 本来就是原地覆盖、不删既有文件（实测过），
REM 所以不需要这个 clean。真要干净发布，请publish 到 TEMP 暂存目录再拷回来。

echo.
echo [2/3] Building ConfigApp...
sc query EWeLinkLinker | findstr /C:"RUNNING" >nul
if %errorlevel%==0 (
    echo.
    echo [ERROR] 服务正在运行，publish\Service 里的 exe 和 dll 会被锁住，
    echo         直接发布只会得到"构建失败"或者半成品安装目录。
    echo         请先在 ConfigApp 里点「停止服务」，或以管理员执行 net stop EWeLinkLinker
    pause
    exit /b 1
)
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
