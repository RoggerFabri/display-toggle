# Remove the installed display-toggle and its login-autostart shortcut.
$ErrorActionPreference = 'SilentlyContinue'

& (Join-Path $PSScriptRoot 'stop.ps1')

$startup = [Environment]::GetFolderPath('Startup')
Remove-Item (Join-Path $startup 'display-toggle.lnk') -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'display-toggle') -Recurse -Force

netsh advfirewall firewall delete rule name="Display Toggle peer link" 2>&1 | Out-Null

Write-Host "-> Uninstalled"
