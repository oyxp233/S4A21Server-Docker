@echo off
setlocal

set "ROOT=%~dp0"
cd /d "%ROOT%"

echo Cleaning .NET build outputs...

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

if exist "%DOTNET%" (
  "%DOTNET%" clean Server\DfoServer.sln -c Debug --nologo -v q
  "%DOTNET%" clean Server\DfoServer.sln -c Release --nologo -v q
) else (
  echo dotnet not found; skipping dotnet clean.
)

call :RemoveDir "%ROOT%dist"
call :RemoveDir "%ROOT%publish"
call :RemoveDir "%ROOT%out"
call :RemoveDir "%ROOT%artifacts"
call :RemoveDir "%ROOT%Server\DfoServer\bin"
call :RemoveDir "%ROOT%Server\DfoServer\obj"
call :RemoveDir "%ROOT%Tool\PvfLib\bin"
call :RemoveDir "%ROOT%Tool\PvfLib\obj"
call :RemoveDir "%ROOT%Tool\DfoPacketMcp\bin"
call :RemoveDir "%ROOT%Tool\DfoPacketMcp\obj"
call :RemoveDir "%ROOT%Tool\DfoPacketMcp\publish"

echo Done.
exit /b 0

:RemoveDir
if exist "%~1\" (
  echo   removing %~1
  rmdir /s /q "%~1"
)
exit /b 0
