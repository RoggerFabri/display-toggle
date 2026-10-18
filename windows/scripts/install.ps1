# Publish display-toggle to %LOCALAPPDATA%\display-toggle and register it to start at
# login via a Startup-folder shortcut. Windows analogue of the macOS launchd agent.
param([string]$Config = 'Release')

$ErrorActionPreference = 'Stop'
$proj = Join-Path (Split-Path -Parent $PSScriptRoot) 'DisplayToggle.csproj'
$dest = Join-Path $env:LOCALAPPDATA 'display-toggle'

# Stop a running instance so its files can be overwritten.
& (Join-Path $PSScriptRoot 'stop.ps1')

Write-Host "Publishing to $dest ..."
dotnet publish $proj -c $Config -r win-x64 --self-contained false -o $dest | Out-Host

$exe = Join-Path $dest 'display-toggle.exe'
if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

# Allow the peer link through the firewall (private networks) so pairing works without a prompt.
# Best-effort: adding an inbound rule needs admin; skip quietly if not elevated.
# The port is the configured PeerPort (Settings.cs: %APPDATA%\display-toggle\config.json).
$port = 51763
try {
    $cfg = Get-Content (Join-Path $env:APPDATA 'display-toggle\config.json') -Raw | ConvertFrom-Json
    if ($cfg.PeerPort -is [int] -and $cfg.PeerPort -gt 0) { $port = $cfg.PeerPort }
} catch { }
try {
    netsh advfirewall firewall delete rule name="Display Toggle peer link" 2>&1 | Out-Null
    netsh advfirewall firewall add rule name="Display Toggle peer link" `
        dir=in action=allow program="$exe" protocol=TCP localport=$port profile=private enable=yes 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "-> Firewall rule added (TCP $port, private)" }
    else { Write-Host "-> Skipped firewall rule (run elevated to add it): allow TCP $port for $exe" }
} catch { Write-Host "-> Could not add firewall rule (add it manually if pairing is blocked)" }

# Login autostart: a shortcut in the user's Startup folder.
$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup 'display-toggle.lnk'
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.Description = 'Display Toggle'
$sc.Save()

Start-Process -FilePath $exe
Write-Host "-> Installed to $exe"
Write-Host "-> Registered for login autostart: $lnk"
