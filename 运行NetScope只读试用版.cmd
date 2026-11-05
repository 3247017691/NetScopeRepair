@echo off
setlocal
set "NETSCOPE_APP=%~dp0artifacts\M1-R1\NetScope.Desktop.exe"
if not exist "%NETSCOPE_APP%" (
  echo NetScope M1-R1 build was not found.
  pause
  exit /b 1
)
start "" "%NETSCOPE_APP%"
endlocal
