@echo off
setlocal
title AtlasNet LAN Firewall Cleanup
echo Removing the LAN access rules added by Enable-Firewall.cmd.
echo Approve the administrator prompt if Windows asks.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); if(-not $admin){$p=Start-Process -FilePath $env:ComSpec -Verb RunAs -Wait -PassThru -ArgumentList '/c','""%~f0" elevated"'; exit $p.ExitCode}; $rules=@(); foreach($name in @('AtlasNet-Demo-Web','AtlasNet-Demo-Game-TCP','AtlasNet-Demo-Game-UDP')){$rule=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue; if($rule){if($rule.Group -ne 'AtlasNet Demo'){throw ('Rule belongs to another firewall group: '+$name)}; $rules+=$rule}}; foreach($rule in $rules){$rule | Remove-NetFirewallRule; Write-Host ('Removed '+$rule.Name)}; if($rules.Count -eq 0){Write-Host 'No AtlasNet demo rules found. Nothing changed.'}; Write-Host 'Windows Firewall and unrelated rules were not changed.'; exit 0 } catch {Write-Error $_; exit 1}"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" (echo Firewall cleanup FAILED.) else (echo Firewall cleanup complete.)
pause
exit /b %RESULT%
