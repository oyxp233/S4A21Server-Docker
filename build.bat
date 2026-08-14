@echo off
setlocal

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" build Server\DfoServer.sln -c Debug
if errorlevel 1 goto failed

echo.
echo Build succeeded.
echo Dev output: Server\DfoServer\bin\Debug\
echo   Run: start-server.bat
echo   Or:  dotnet run --project Server\DfoServer\DfoServer.csproj -c Debug
echo.
echo For a self-contained release build: publish.bat
pause
exit /b 0

:failed
echo.
echo Build failed. Please install the .NET SDK, then run build.bat again.
echo Download: https://aka.ms/dotnet-download
pause
exit /b 1