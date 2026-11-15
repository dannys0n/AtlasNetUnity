@echo off
setlocal
set "ATLASNET_RULE_SCOPE=Demo"
if /I "%~1"=="NativeSoccer" set "ATLASNET_RULE_SCOPE=NativeSoccer"
title AtlasNet - Allow Demo Connections
echo Adding firewall allowances for demo connections. Windows Firewall stays enabled.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Firewall.ps1" -Action Enable -Scope "%ATLASNET_RULE_SCOPE%"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Firewall operation FAILED. Review the message above.
pause
exit /b %RESULT%
