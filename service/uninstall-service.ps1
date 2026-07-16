# Zatrzymuje i wyrejestrowuje usluge Przepustnica.
# Uruchom jako administrator:  powershell -ExecutionPolicy Bypass -File uninstall-service.ps1

$ErrorActionPreference = "Stop"
$serviceName = "Przepustnica"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Uruchom ten skrypt jako administrator."
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName
    Write-Host "OK - usluga wyrejestrowana. Dane w %ProgramData%\Przepustnica zostaja."
} else {
    Write-Host "Usluga $serviceName nie jest zarejestrowana."
}
