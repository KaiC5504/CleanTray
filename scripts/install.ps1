#Requires -Version 7.0
# Builds CleanTray and installs it for the current user. No admin needed.
# Explorer is never restarted: the app stages the hook DLL by content hash before injecting it,
# so a copy still pinned in Explorer never blocks overwriting the install folder.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\CleanTray')
)

$ErrorActionPreference = 'Stop'
# Resolved now, against the caller's PowerShell location (not the process's current directory,
# which PowerShell does not keep in step), before anything changes directory.
$InstallDir = [IO.Path]::GetFullPath($InstallDir, $PWD.ProviderPath)
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'publish'

function Wait-Exit([Diagnostics.Process]$process, [int]$seconds, [string]$what) {
    if (-not $process.WaitForExit($seconds * 1000)) { throw "$what did not finish within $seconds s" }
    $process.ExitCode
}

# build.ps1 changes directory to the repo.
Push-Location
try {
    & (Join-Path $repo 'build.ps1')
    if ($LASTEXITCODE) { throw "build.ps1 failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $repo 'src\CleanTray') -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $repo 'build\src\CleanTrayHook\Release\CleanTrayHook.dll') $publish -Force

$running = @(Get-Process CleanTray -ErrorAction SilentlyContinue)
if ($running) {
    Write-Host 'Stopping CleanTray'
    # --exit lets it hand every captured icon back to the tray before it goes.
    $exe = $running[0].Path
    if ($exe) { Wait-Exit (Start-Process $exe -ArgumentList '--exit' -PassThru) 5 'CleanTray --exit' | Out-Null }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Process CleanTray -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    # Killing it is still safe: the hook releases everything when CleanTray's pipe drops.
    # It may exit on its own between the check and the kill.
    Get-Process CleanTray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $publish '*') $InstallDir -Recurse -Force
Set-Content (Join-Path $InstallDir 'installed.marker') "Installed by scripts/install.ps1 on $(Get-Date -Format s)"

$installed = Join-Path $InstallDir 'CleanTray.exe'
$exit = Wait-Exit (Start-Process $installed -ArgumentList '--register-autostart' -PassThru) 30 'CleanTray --register-autostart'
if ($exit) { Write-Warning "CleanTray --register-autostart exited $exit" }

Start-Process $installed -WorkingDirectory $InstallDir
Write-Host "CleanTray installed to $InstallDir and started"
