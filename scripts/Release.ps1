# Builds a release and publishes it on GitHub, where installed copies update from:
#   - Setup.exe and the Velopack update files for Windows (self-contained, per-user install)
#   - Baton.apk, signed with the key in android\keystore.properties
# Not the Zen/Firefox extension: its build carries one PC's private browser token
# (Build-ZenExtension.ps1), so each PC needs its own.
# Needs: the GitHub CLI signed in (gh auth login), Velopack's tool (dotnet tool install -g vpk),
# and android\keystore.properties. -NoPublish builds everything but leaves GitHub alone.
# .github\workflows\release.yml runs this on every push to main.
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must look like 1.2.3.' }

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\release'
$repository = 'https://github.com/TheTobiTiger02/Baton'
$packId = 'Baton.App'   # Updates.PackageId: the install folder under %LOCALAPPDATA%

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw 'Install Velopack first: dotnet tool install -g vpk' }
if (-not (Test-Path (Join-Path $root 'android\keystore.properties'))) {
    throw 'android\keystore.properties is missing: every release must be signed with the same key, or phones cannot update.'
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

# One version for everything: the PC app reads it from Directory.Build.props, Android from -PbatonVersion.
$props = Join-Path $root 'Directory.Build.props'
(Get-Content $props -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $props -NoNewline -Encoding utf8

dotnet test (Join-Path $root 'tests\Baton.Tests') --nologo
if ($LASTEXITCODE -ne 0) { throw 'PC tests failed.' }

$windows = Join-Path $out 'windows'
dotnet publish (Join-Path $root 'src\Baton.App\Baton.App.csproj') -c Release -r win-x64 --self-contained true -o $windows --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }

# The previous release lets Velopack make a small delta update; the first release has none.
$packages = Join-Path $out 'velopack'
$token = if ($env:GITHUB_TOKEN) { @('--token', $env:GITHUB_TOKEN) } else { @() }
vpk download github --repoUrl $repository --outputDir $packages @token 2>$null
vpk pack --packId $packId --packVersion $Version --packDir $windows --mainExe 'Baton.exe' --packTitle 'Baton' `
    --icon (Join-Path $root 'src\Baton.App\Assets\Baton.ico') --outputDir $packages
if ($LASTEXITCODE -ne 0) { throw 'Packing the Windows installer failed.' }

Push-Location (Join-Path $root 'android')
try {
    .\gradlew.bat :app:testDebugUnitTest :app:assembleRelease "-PbatonVersion=$Version" --console=plain
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
} finally {
    Pop-Location
}
Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') (Join-Path $out 'Baton.apk')

# Everything vpk made, minus the previous release's package it downloaded for the delta.
$assets = @(Get-ChildItem $packages -File | Where-Object { $_.Extension -ne '.nupkg' -or $_.Name -like "*-$Version-*" } |
    ForEach-Object FullName) + (Join-Path $out 'Baton.apk')
Write-Host "Release $Version built:"
$assets | ForEach-Object { Write-Host "  $_" }

if ($NoPublish) { return }
# In CI the release is tagged on the commit that was pushed, not whatever main is by then.
$target = if ($env:GITHUB_SHA) { @('--target', $env:GITHUB_SHA) } else { @() }
gh release create "v$Version" @assets --repo $repository --title "Baton $Version" --generate-notes @target
if ($LASTEXITCODE -ne 0) { throw 'Publishing the release failed.' }
