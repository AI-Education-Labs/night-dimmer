# Compiles NightDimmer.exe using the C# compiler bundled with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found - .NET Framework 4.x is required (it ships with Windows)." }

$src  = Join-Path $PSScriptRoot 'NightDimmer.cs'
$out  = Join-Path $PSScriptRoot 'NightDimmer.exe'
$icon = Join-Path $PSScriptRoot 'moon.ico'
if (-not (Test-Path $icon)) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-icon.ps1') }

& $csc /nologo /target:winexe /optimize+ /out:$out /win32icon:$icon /r:System.Windows.Forms.dll /r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Built $out"
