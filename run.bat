@echo off
REM Pinta 本地启动脚本（优先用 dotnet.exe 加载 DLL，绕过 apphost 的 roll-forward 问题）
REM 用法：双击运行
cd /d "%~dp0"
set DOTNET_ROLL_FORWARD=LatestMinor
set DOTNET_ROLL_FORWARD_TO_PRERELEASE=1
cd build\bin
echo [Pinta] Launching using dotnet.exe...
echo [Pinta] Press Ctrl+C to close
dotnet Pinta.dll
pause
