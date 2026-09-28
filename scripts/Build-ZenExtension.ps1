# Builds the Baton browser extension for Zen and other Firefox-based browsers:
# .\artifacts\browser\baton-zen.xpi, from the same sources as the Chromium extension.
#
# Differences from the Chromium build: a Manifest V2 manifest with a persistent background page
# (what Firefox runs unsigned add-ons with most reliably), the promise-based browser.* API, and
# this PC's token in config.js: Firefox gives each extension a random origin, so Baton recognises
# its own extension by the token instead (see BrowserBridge.TokenPath).
param([string]$Output)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'browser-extension'
if (-not $Output) { $Output = Join-Path $root 'artifacts\browser\baton-zen.xpi' }

$storage = Join-Path $env:LOCALAPPDATA 'Baton'
$tokenPath = Join-Path $storage 'browser-token'
if (-not (Test-Path $tokenPath)) {
    New-Item -ItemType Directory -Force $storage | Out-Null
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($tokenPath, (($bytes | ForEach-Object { $_.ToString('x2') }) -join ''))
}
$token = [IO.File]::ReadAllText($tokenPath).Trim()

$chromium = Get-Content (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
$manifest = [ordered]@{
    manifest_version = 2
    name = $chromium.name
    description = $chromium.description
    version = $chromium.version
    browser_specific_settings = @{ gecko = @{ id = 'baton@baton.dev'; strict_min_version = '115.0' } }
    permissions = @('tabs', 'contextMenus', 'alarms', '<all_urls>')
    background = @{ scripts = @('config.js', 'background.js'); persistent = $true }
    content_scripts = @(@{ matches = @('http://*/*', 'https://*/*'); js = @('content.js'); run_at = 'document_idle' })
    browser_action = @{ default_popup = 'popup.html'; default_title = $chromium.action.default_title; default_icon = $chromium.action.default_icon }
    icons = $chromium.icons
}

$files = [ordered]@{
    'manifest.json' = ($manifest | ConvertTo-Json -Depth 6)
    'config.js' = "globalThis.BATON_TOKEN = `"$token`";`n"
    # Firefox's chrome.* is callback-based; the scripts await the calls, which browser.* supports.
    'background.js' = (Get-Content (Join-Path $source 'background.js') -Raw) -replace '\bchrome\.', 'browser.'
    'content.js' = (Get-Content (Join-Path $source 'content.js') -Raw) -replace '\bchrome\.', 'browser.'
    'popup.html' = Get-Content (Join-Path $source 'popup.html') -Raw
    'popup.js' = Get-Content (Join-Path $source 'popup.js') -Raw
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
if (Test-Path $Output) { Remove-Item $Output -Force }
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($Output, [IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    foreach ($name in $files.Keys) {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry($name).Open(), $utf8)
        $writer.Write($files[$name])
        $writer.Dispose()
    }
    # Entry names use forward slashes: Firefox rejects archives with backslashes.
    foreach ($icon in Get-ChildItem (Join-Path $source 'icons') -File) {
        $entry = $zip.CreateEntry("icons/$($icon.Name)").Open()
        $bytes = [IO.File]::ReadAllBytes($icon.FullName)
        $entry.Write($bytes, 0, $bytes.Length)
        $entry.Dispose()
    }
} finally {
    $zip.Dispose()
    $stream.Dispose()
}
Write-Host "Zen extension: $Output"
