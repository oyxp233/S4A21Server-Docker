@echo off
setlocal EnableDelayedExpansion
set "ROOT=%~dp0"
cd /d "%ROOT%"

set "SERVER_DIR=%ROOT%Server\DfoServer\bin\Debug"

echo ================================
echo  Step 1/4: Building DfoServer...
echo ================================
dotnet build "%ROOT%Server\DfoServer.sln" -c Debug
if %ERRORLEVEL% neq 0 (
    echo Build failed with error code %ERRORLEVEL%
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ================================
echo  Step 2/4: Building 86JP.dll (client patch)...
echo ================================
echo  [2/4] Checking build tools...
if exist "%ROOT%Patch\86JP.sln" (
    echo  [2/4] Building with MSBuild...
    start /b /wait "" "%MSBuild%" "%ROOT%Patch\86JP.sln" /p:Configuration=Debug /p:Platform=x86 /t:Rebuild /nologo
    if %ERRORLEVEL% equ 0 (
        echo  [2/4] 86JP.dll built successfully.
    ) else (
        echo  [2/4] Build not required, using existing 86JP.dll.
    )
) else (
    echo  [2/4] No Patch project found, skipping.
)

echo.
echo ================================
echo  Step 3/4: Building PvfProxy...
echo ================================
dotnet build "%ROOT%Tool\PvfProxy\PvfProxy.csproj" -c Release --nologo
if %ERRORLEVEL% neq 0 (
    echo Failed to build PvfProxy. Make sure .NET 10 SDK is installed.
    pause
    exit /b %ERRORLEVEL%
)
set "PVFPROXY=%ROOT%Tool\PvfProxy\bin\Release\net10.0\PvfProxy.exe"

echo.
echo ================================
echo  Step 4/4: Starting PvfProxy + DfoServer + Game...
echo ================================
echo.
echo  [Proxy] PvfProxy listens on 7001/10011
echo  [Proxy] Forwards to server on 7002/10012

set "CAPTURE_DIR=%ROOT%\Server\DfoServer\bin\Debug\capture_logs"
if not exist "%CAPTURE_DIR%" mkdir "%CAPTURE_DIR%"

taskkill /f /im DNF.exe >nul 2>&1
taskkill /f /im PvfProxy.exe >nul 2>&1
taskkill /f /im DfoServer.exe >nul 2>&1
del /f /q "%CAPTURE_DIR%\pvfproxy_*.log" "%CAPTURE_DIR%\packet_log.txt" 2>nul
timeout /t 1 /nobreak >nul

start "" "%SERVER_DIR%\DfoServer.exe" --server-ip "127.0.0.1" --packet-capture "%CAPTURE_DIR%" --proxy
timeout /t 2 /nobreak >nul

start "" /d "%CAPTURE_DIR%" "%PVFPROXY%" --log-dir "%CAPTURE_DIR%"
timeout /t 2 /nobreak >nul

netstat -ano | findstr /R "TCP.*:7001 .*LISTENING" >nul
if %ERRORLEVEL% neq 0 (
    echo.
    echo  ERROR: PvfProxy failed to start on port 7001.
    echo  Check that .NET 10 runtime is installed and no other process uses 7001/10011.
    echo  Proxy log: %CAPTURE_DIR%\pvfproxy_*.log
    taskkill /f /im DfoServer.exe >nul 2>&1
    pause
    exit /b 1
)

echo.
echo  Starting game client...
echo.
:: ★ 修改 CLIENT_DIR 为你的游戏客户端路径
:: ★ bat 处理中文文件名可能乱码，游戏启动脚本请改名为 StartGame.bat
set "CLIENT_DIR=%ROOT%DXF"
cd /d "%CLIENT_DIR%"
start "" "StartGame.bat"

echo  Waiting for game client to exit (including crashes)...
:wait_loop
timeout /t 2 /nobreak >nul
tasklist /fi "IMAGENAME eq DNF.exe" 2>nul | find /i "DNF.exe" >nul
if %ERRORLEVEL% equ 0 goto wait_loop

echo.
echo DNF.exe exited. Cleaning up...
taskkill /f /im DNF.exe >nul 2>&1
taskkill /f /im PvfProxy.exe >nul 2>&1
taskkill /f /im DfoServer.exe >nul 2>&1
echo Done.
echo Logs saved:
echo   Server capture: %CAPTURE_DIR%\packet_log.txt
echo   Proxy capture:  %CAPTURE_DIR%\pvfproxy_*.log
exit /b 0
