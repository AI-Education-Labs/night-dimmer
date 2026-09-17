# Removes Night Dimmer: app files, shortcuts, start-with-Windows entry, settings and log.
$Install = Join-Path $env:LOCALAPPDATA 'Programs\NightDimmer'
$exe = Join-Path $Install 'NightDimmer.exe'

if (Test-Path $exe) { try { & $exe --exit; Start-Sleep 1 } catch { } }
Get-Process NightDimmer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name NightDimmer -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\NightDimmer' -Recurse -Force -ErrorAction SilentlyContinue
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    Remove-Item (Join-Path $dir 'Night Dimmer.lnk') -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $env:APPDATA 'NightDimmer.log') -Force -ErrorAction SilentlyContinue
Remove-Item $Install -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Night Dimmer removed. (The display returns to normal the moment the app exits.)'
