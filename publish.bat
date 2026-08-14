@echo off
setlocal

set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

set "CONFIG=Release"
if not "%~1"=="" set "CONFIG=%~1"

set "RID=win-x64"
set "OUT=%~dp0dist\%RID%"

echo Publishing self-contained single-file build for %RID% -^> %OUT%
"%DOTNET%" publish "%~dp0Server\DfoServer\DfoServer.csproj" ^
  -c %CONFIG% ^
  -r %RID% ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%OUT%"
if errorlevel 1 goto failed

echo.
echo Done. Run:
echo   dist\%RID%\DfoServer.exe
echo or double-click StartServer.exe after rebuilding it for dist\%RID%.
pause
exit /b 0

:failed
echo Publish failed.
pause
exit /b 1
