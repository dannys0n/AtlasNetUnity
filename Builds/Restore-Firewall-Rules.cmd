@echo off
setlocal
title AtlasNet - Restore Firewall Rules
echo Removing demo firewall allowances. Unrelated rules stay unchanged.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Firewall.ps1" -Action Disable
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Firewall operation FAILED. Review the message above.
pause
exit /b %RESULT%
