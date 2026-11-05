@echo off
setlocal
set "NETSCOPE_APP=%~dp0artifacts\M1-R2\NetScope.Desktop.exe"
if not exist "%NETSCOPE_APP%" (
  echo NetScope M1-R2 build was not found.
  pause
  exit /b 1
)
start "" "%NETSCOPE_APP%"
endlocal
