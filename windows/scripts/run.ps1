# Stop any running instance, rebuild, then launch — the analogue of `make run` on macOS.
# Stopping first releases the lock on the exe so the build can overwrite it.
param([string]$Config = 'Release')

$root = Split-Path -Parent $PSScriptRoot   # windows/

& (Join-Path $PSScriptRoot 'stop.ps1')
Start-Sleep -Milliseconds 300
$ErrorActionPreference = 'Stop'

dotnet build (Join-Path $root 'DisplayToggle.csproj') -c $Config | Out-Host

$exe = Join-Path $root "bin\$Config\net8.0-windows\display-toggle.exe"
if (-not (Test-Path $exe)) { throw "not built: $exe" }
Start-Process -FilePath (Resolve-Path $exe)
Write-Host "-> launched display-toggle"
