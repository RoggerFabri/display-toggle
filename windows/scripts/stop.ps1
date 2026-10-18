# Stop a running display-toggle. Ask it to exit cleanly first (it then removes its tray icon);
# force-kill only as a fallback, since a killed process leaves a ghost icon in the tray until
# the mouse passes over it. Shared by run.ps1, install.ps1 and uninstall.ps1.
# Global\ since the quit event moved there; Local\ still reaches builds from before that.
foreach ($name in 'Global\display-toggle.quit', 'Local\display-toggle.quit') {
    $quit = $null
    if ([System.Threading.EventWaitHandle]::TryOpenExisting($name, [ref]$quit)) {
        $null = $quit.Set()
        $quit.Dispose()
    }
}
Get-Process display-toggle -ErrorAction SilentlyContinue | Wait-Process -Timeout 3 -ErrorAction SilentlyContinue
Get-Process display-toggle -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
