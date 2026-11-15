param([ValidateSet('Enable','Disable')][string]$Action='Enable',[switch]$Elevated,[ValidateSet('Demo','NativeSoccer')][string]$Scope='Demo')
$ErrorActionPreference='Stop'
$logFolder=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AtlasNetDemo'
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$log=Join-Path $logFolder 'firewall-last-operation.txt'
function Report([string]$Message) { Write-Host $Message; Add-Content -LiteralPath $log -Value $Message }
$admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(!$admin) {
    Write-Host 'Administrator approval is required. Waiting for the Windows approval prompt...'
    try {
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Action '+$Action+' -Scope '+$Scope+' -Elevated'
        $process=Start-Process -FilePath (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/powershell.exe') -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if(Test-Path -LiteralPath $log) {Get-Content -LiteralPath $log}
        exit $process.ExitCode
    } catch {Write-Host ('Administrator approval failed or was cancelled: '+$_.Exception.Message);exit 1}
}
Set-Content -LiteralPath $log -Value ("$Action started: "+(Get-Date -Format s))
try {
    $definitions=@(@('AtlasNet-Demo-Web','TCP',8094),@('AtlasNet-Demo-Game-TCP','TCP',7777),@('AtlasNet-Demo-Game-UDP','UDP',7777))
    $remoteAddress = 'Any'
    if ($Scope -eq 'NativeSoccer') {
        $definitions = @(@('AtlasNet-NativeSoccer-Web','TCP',8094),@('AtlasNet-NativeSoccer-WebSocket','TCP',5921))
        $remoteAddress = 'LocalSubnet'
    }
    # Preflight all reserved names before changing any rule.
    $existing=@{}
    foreach($definition in $definitions) {
        Report ('Checking '+$definition[0]+'...')
        $rule=Get-NetFirewallRule -Name $definition[0] -ErrorAction SilentlyContinue
        if($rule -and $rule.Group -ne 'AtlasNet Demo') {throw ('Reserved rule belongs to another group: '+$definition[0])}
        $existing[$definition[0]]=$rule
    }
    foreach($definition in $definitions) {
        $name=$definition[0]
        Report ("$Action $name ($($definition[1]) $($definition[2]))...")
        if($existing[$name]) {Remove-NetFirewallRule -Name $name -ErrorAction Stop}
        if($Action -eq 'Enable') {
            New-NetFirewallRule -Name $name -DisplayName $name -Group 'AtlasNet Demo' -Direction Inbound -Action Allow -Protocol $definition[1] -LocalPort $definition[2] -Profile Any -RemoteAddress $remoteAddress -ErrorAction Stop | Out-Null
            $verified=Get-NetFirewallRule -Name $name -ErrorAction Stop
            if($verified.Enabled -ne 'True' -or $verified.Action -ne 'Allow') {throw "Verification failed: $name"}
        }
    }
    Report "$Action complete. Windows Firewall remains enabled; unrelated rules were not changed."
    Report ("Log: "+$log)
    exit 0
} catch {Report ('FAILED: '+$_.Exception.Message);Report ("Log: "+$log);exit 1}
