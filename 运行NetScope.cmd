@echo off
chcp 65001 >nul
setlocal
rem NetScope Repair - full build with real, reversible quick repair.
set "NETSCOPE_APP=%~dp0artifacts\M3-QuickRepair\NetScope.Desktop.exe"
if not exist "%NETSCOPE_APP%" (
  echo Release build not found.
  echo Rebuild with: dotnet publish src/NetScope.Desktop -c Release -o artifacts/M3-QuickRepair --no-self-contained
  pause
  exit /b 1
)
start "" "%NETSCOPE_APP%"
endlocal
