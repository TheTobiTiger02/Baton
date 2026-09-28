# Builds the Windows app and the Android APK into .\artifacts, then installs the PC app
# (scripts\Install.ps1) unless -NoInstall is given.
param(
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

dotnet test (Join-Path $root 'tests\Baton.Tests') --nologo
if ($LASTEXITCODE -ne 0) { throw 'PC tests failed.' }

# A copy started from the build folder locks the files that publish replaces.
Get-Process -Name 'Baton' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path $artifacts 'windows'), 'OrdinalIgnoreCase') } |
    ForEach-Object { Stop-Process -Id $_.Id -Force; $_.WaitForExit(10000) | Out-Null }

dotnet publish (Join-Path $root 'src\Baton.App\Baton.App.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $artifacts 'windows') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }

Push-Location (Join-Path $root 'android')
try {
    .\gradlew.bat :app:testDebugUnitTest :app:assembleDebug --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
} finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'Build-ZenExtension.ps1') -Output (Join-Path (Join-Path $artifacts 'browser') 'baton-zen.xpi')

New-Item -ItemType Directory -Force (Join-Path $artifacts 'android') | Out-Null
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk') (Join-Path $artifacts 'android\Baton.apk') -Force
Write-Host "Done: $artifacts"

if (-not $NoInstall) {
    & (Join-Path $PSScriptRoot 'Install.ps1')
}
