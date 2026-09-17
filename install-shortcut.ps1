# Puts a "Night Dimmer" shortcut on the desktop (and in the Start menu).
$exe = Join-Path $PSScriptRoot 'NightDimmer.exe'
if (-not (Test-Path $exe)) { throw "Build first: powershell -ExecutionPolicy Bypass -File build.ps1" }
$sh = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $sh.CreateShortcut((Join-Path $dir 'Night Dimmer.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Dim and warm the screen for night viewing (Ctrl+Alt+D for controls)'
    $lnk.Save()
    Write-Host "Created $($lnk.FullName)"
}
