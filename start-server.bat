@echo off
setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
cd /d "%ROOT%"

set "SERVER_IP=%SERVER_IP%"
set "EXTRA_ARGS="

:parse_args
if "%~1"=="" goto after_parse
if /I "%~1"=="--server-ip" (
  set "SERVER_IP=%~2"
  shift
  shift
  goto parse_args
)
if /I "%~1"=="--help" (
  call :usage
  exit /b 0
)
set "EXTRA_ARGS=!EXTRA_ARGS! %~1"
shift
goto parse_args

:after_parse
if not defined SERVER_IP set "SERVER_IP=127.0.0.1"
if /I "%SERVER_IP%"=="auto" (
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command ^
    "(Get-NetIPAddress -AddressFamily IPv4 ^| Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } ^| Select-Object -First 1 -ExpandProperty IPAddress)"`) do set "SERVER_IP=%%I"
  if not defined SERVER_IP (
    echo Could not detect LAN IP. Set SERVER_IP manually.
    exit /b 1
  )
)

set "SERVER_EXE="
if exist "%ROOT%DfoServer.exe" set "SERVER_EXE=%ROOT%DfoServer.exe"
if not defined SERVER_EXE if exist "%ROOT%dist\win-x64\DfoServer.exe" set "SERVER_EXE=%ROOT%dist\win-x64\DfoServer.exe"
if not defined SERVER_EXE if exist "%ROOT%Server\DfoServer\bin\Debug\DfoServer.exe" set "SERVER_EXE=%ROOT%Server\DfoServer\bin\Debug\DfoServer.exe"

if not defined SERVER_EXE (
  echo DfoServer was not found.
  echo Publish first: publish.bat
  exit /b 1
)

for %%I in ("%SERVER_EXE%") do set "SERVER_DIR=%%~dpI"
set "SERVER_IP=%SERVER_IP%"

echo Using SERVER_IP=%SERVER_IP%
cd /d "%SERVER_DIR%"
"%SERVER_EXE%" --server-ip "%SERVER_IP%" %EXTRA_ARGS%
exit /b %ERRORLEVEL%

:usage
echo Usage:
echo   start-server.bat [--server-ip ^<ip^|auto^>] [extra server args...]
echo   set SERVER_IP=^<ip^> ^& start-server.bat
echo.
echo   --server-ip   IP sent to the game client. Default: 127.0.0.1
echo   auto          Pick this machine's LAN IPv4
exit /b 0
