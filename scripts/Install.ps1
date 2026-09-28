# Installs the built PC app from .\artifacts\windows for the current user:
# %LOCALAPPDATA%\Programs\Baton, a Start menu entry (so Windows search finds "Baton"), then starts it.
# Baton points its own autostart at the installed copy when it starts.
# -Uninstall removes the app, the Start menu entry and autostart; settings and pairings stay.
param(
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'artifacts\windows'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Baton'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Baton.lnk'
$exe = Join-Path $target 'Baton.exe'

function Stop-Baton {
    $running = @(Get-Process -Name 'Baton' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path.StartsWith($target, 'OrdinalIgnoreCase') -or $_.Path.StartsWith($source, 'OrdinalIgnoreCase'))
    })
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(10000) | Out-Null
    }
}

Stop-Baton

if ($Uninstall) {
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Baton' -ErrorAction SilentlyContinue
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Baton removed. Settings and pairings are kept in' (Join-Path $env:LOCALAPPDATA 'Baton')
    return
}

if (-not (Test-Path (Join-Path $source 'Baton.exe'))) {
    throw "No build in $source. Run scripts\Build.ps1 first."
}

robocopy $source $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying to $target failed (robocopy $LASTEXITCODE)." }
$global:LASTEXITCODE = 0

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Continue what you are doing on your phone or PC'
$link.Save()

Start-Process -FilePath $exe -ArgumentList '--background'
Write-Host "Installed to $target. Search the Start menu for 'Baton'."
