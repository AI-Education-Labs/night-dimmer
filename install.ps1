<#
  Night Dimmer installer - builds the app on your own PC and adds shortcuts.

  One-line install (PowerShell):
      irm https://raw.githubusercontent.com/AI-Education-Labs/night-dimmer/main/install.ps1 | iex

  Or from a cloned repo:
      powershell -ExecutionPolicy Bypass -File install.ps1

  Why compile locally? Night Dimmer is one C# file, and every Windows PC already has the
  compiler (.NET Framework's csc.exe). Building it here means no download of an unsigned
  .exe, no SmartScreen warning, and you can read exactly what you're running.
#>
$ErrorActionPreference = 'Stop'
$Repo    = 'AI-Education-Labs/night-dimmer'
$Branch  = 'main'
$Raw     = "https://raw.githubusercontent.com/$Repo/$Branch"
$Install = Join-Path $env:LOCALAPPDATA 'Programs\NightDimmer'
$Files   = 'NightDimmer.cs', 'make-icon.ps1', 'build.ps1', 'uninstall.ps1', 'LICENSE'

Write-Host ''
Write-Host '  Night Dimmer  -  dim, warm and blue-filter your screen at night' -ForegroundColor Yellow
Write-Host '  by AI Education Labs  -  MIT License' -ForegroundColor DarkGray
Write-Host ''

# 1. Get the source: from this folder if we're inside the repo, otherwise from GitHub.
New-Item -ItemType Directory -Force $Install | Out-Null
$local = if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'NightDimmer.cs'))) { $PSScriptRoot } else { $null }
foreach ($f in $Files) {
    if ($local) { Copy-Item (Join-Path $local $f) (Join-Path $Install $f) -Force }
    else {
        Write-Host "  downloading $f"
        Invoke-WebRequest -UseBasicParsing "$Raw/$f" -OutFile (Join-Path $Install $f)
    }
}

# 2. Stop a running copy, then compile.
$exe = Join-Path $Install 'NightDimmer.exe'
$running = Get-Process NightDimmer -ErrorAction SilentlyContinue | Select-Object -First 1
if ($running) {
    try { & $running.Path --exit } catch { }   # asks it to restore the display and quit
    Start-Sleep 1
    Get-Process NightDimmer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Write-Host '  compiling...'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Install 'build.ps1') | Out-Null
if (-not (Test-Path $exe)) { throw 'Build failed. Is .NET Framework 4.x present? (It ships with Windows 10/11.)' }

# 3. Shortcuts on the desktop and in the Start menu.
$sh = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $sh.CreateShortcut((Join-Path $dir 'Night Dimmer.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $Install
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Dim and warm the screen for night viewing (Ctrl+Alt+D for controls)'
    $lnk.Save()
}

# 4. Launch.
Start-Process $exe
Write-Host ''
Write-Host "  Installed to $Install" -ForegroundColor Green
Write-Host '  Running now - look for the amber moon in the system tray.'
Write-Host ''
Write-Host '  Ctrl+Alt+D   control panel        Ctrl+Alt+0   filter on/off'
Write-Host '  Ctrl+Alt+-   darker               Ctrl+Alt+=   brighter'
Write-Host '  Ctrl+Alt+9   cycle warmth         Ctrl+Alt+8   cycle blue-light filter'
Write-Host ''
Write-Host "  Uninstall:  powershell -ExecutionPolicy Bypass -File `"$Install\uninstall.ps1`"" -ForegroundColor DarkGray
Write-Host ''
