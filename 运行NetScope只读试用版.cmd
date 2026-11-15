@echo off
chcp 65001 >nul
setlocal
rem Legacy entry. The read-only trial build has been superseded; this now launches the real build.
echo 旧「只读试用版」入口已停用，正在启动带真实修复的版本（运行NetScope.cmd）。
call "%~dp0运行NetScope.cmd"
endlocal
