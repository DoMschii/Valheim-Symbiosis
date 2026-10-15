$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PackageRoot = $PSScriptRoot
$DevRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Valheim-Symbiosis-Dev'
$ModSource = Join-Path $DevRoot 'ModSource'

if (-not (Test-Path $ModSource)) {
    throw "ModSource nicht gefunden: $ModSource"
}

Write-Host '==> Uebernehme v4.5.2 / Ego v1.6.50 in die dauerhafte ModSource' -ForegroundColor Cyan

$copies = @(
    @{ From='src\Ego\EgoFacingLockPatch.cs'; To='src\Ego\EgoFacingLockPatch.cs' },
    @{ From='src\Ego\Setup.ps1'; To='src\Ego\Setup.ps1' },
    @{ From='src\Ego\README.txt'; To='src\Ego\README.txt' },
    @{ From='src\Mount\Valheim_Workers_Mount.csproj'; To='src\Mount\Valheim_Workers_Mount.csproj' },
    @{ From='Setup.ps1'; To='Setup.ps1' },
    @{ From='CHANGELOG.md'; To='CHANGELOG.md' }
)

foreach ($item in $copies) {
    $from = Join-Path $PackageRoot $item.From
    $to = Join-Path $ModSource $item.To
    if (-not (Test-Path $from)) { throw "Paketdatei fehlt: $from" }
    $dir = Split-Path $to -Parent
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item $from $to -Force
}

Write-Host '==> Baue/installiere nur Valheim Ego v1.6.50' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ModSource 'src\Ego\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Ego Setup fehlgeschlagen (ExitCode $LASTEXITCODE)." }

Write-Host ''
Write-Host 'FERTIG: v4.5.2 Source aktualisiert + Ego v1.6.50 installiert.' -ForegroundColor Green
Write-Host 'Test: Ego -> S halten, dabei bauen sowie angreifen/blocken. Kein 180-Grad-Flackern mehr.' -ForegroundColor Yellow
