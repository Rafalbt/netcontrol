# Rejestruje Przepustnice jako usluge Windows z autostartem.
# Uruchom jako administrator:  powershell -ExecutionPolicy Bypass -File install-service.ps1
# Domyslnie uzywa builda z dist\service:
#   dotnet publish -c Release -r win-x64 --self-contained true -o ..\dist\service

param(
    [string]$BinDir = (Join-Path $PSScriptRoot "..\dist\service")
)

$ErrorActionPreference = "Stop"
$serviceName = "Przepustnica"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Uruchom ten skrypt jako administrator."
}

$exePath = (Resolve-Path (Join-Path $BinDir "PrzepustnicaService.exe")).Path

# Zatrzymaj instancje deweloperskie (konsolowe `dotnet run`) i stara usluge.
Get-Process -Name PrzepustnicaService -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $serviceName `
    -BinaryPathName "`"$exePath`"" `
    -DisplayName "Przepustnica" `
    -Description "Limity przepustowosci sieci per aplikacja (monitoring ETW + WinDivert)." `
    -StartupType Automatic | Out-Null

# Restart po awarii: 5 s / 10 s / 30 s, reset licznika po dobie.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Start-Service -Name $serviceName
Get-Service -Name $serviceName | Format-Table Name, Status, StartType -AutoSize
Write-Host "OK - usluga zarejestrowana i uruchomiona. UI polaczy sie automatycznie."
