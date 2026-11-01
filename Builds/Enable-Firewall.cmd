@echo off
setlocal
title AtlasNet LAN Firewall Setup
powershell -NoProfile -ExecutionPolicy Bypass -Command "$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); if(-not $admin){$p=Start-Process -FilePath $env:ComSpec -Verb RunAs -Wait -PassThru -ArgumentList '/c','""%~f0" elevated"'; exit $p.ExitCode}; try { $rules=@(@('AtlasNet-Demo-Web','TCP','8094'),@('AtlasNet-Demo-Game-TCP','TCP','7777'),@('AtlasNet-Demo-Game-UDP','UDP','7777')); foreach($r in $rules){$existing=Get-NetFirewallRule -Name $r[0] -ErrorAction SilentlyContinue;if($existing){Remove-NetFirewallRule -Name $r[0]};New-NetFirewallRule -Name $r[0] -DisplayName $r[0] -Group 'AtlasNet Demo' -Direction Inbound -Action Allow -Protocol $r[1] -LocalPort $r[2] -Profile Any -RemoteAddress Any | Out-Null}; Write-Host 'Enabled HTTP TCP 8094 and game TCP/UDP 7777 on all network profiles.';Write-Host 'To undo, run Disable-Firewall.cmd.';exit 0 } catch {Write-Error $_;exit 1}"
if errorlevel 1 (echo Firewall setup FAILED.) else (echo Firewall setup complete.)
pause
