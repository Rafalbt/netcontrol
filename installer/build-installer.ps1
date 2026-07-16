# Buduje kompletny installer MSI: UI (Tauri release) + usluga (publish) + WiX.
# Wymagania: node+npm, cargo (rustup), dotnet SDK 8, wix 5 (dotnet tool install --global wix --version 5.0.2).
# Usluga jest publikowana self-contained (win-x64) - target NIE potrzebuje .NET Runtime.
# UI potrzebuje WebView2 (Win10/11 maja domyslnie). Podpis kodu: TODO (Etap 4).

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:USERPROFILE\.dotnet\tools;C:\Program Files\dotnet;$env:PATH"

Write-Host "[1/3] UI (tauri build)..."
Push-Location (Join-Path $root "ui")
try {
    npm run tauri build -- --no-bundle
    if ($LASTEXITCODE -ne 0) { throw "tauri build failed" }
} finally { Pop-Location }

Write-Host "[2/3] Usluga (dotnet publish)..."
Push-Location (Join-Path $root "service\PrzepustnicaService")
try {
    dotnet publish -c Release -r win-x64 --self-contained true -o (Join-Path $root "dist\service")
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
} finally { Pop-Location }

Write-Host "[3/3] MSI (wix build)..."
Push-Location $PSScriptRoot
try {
    wix build Przepustnica.wxs -arch x64 -o Przepustnica-0.1.0.msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
} finally { Pop-Location }

Write-Host "OK -> $PSScriptRoot\Przepustnica-0.1.0.msi"
