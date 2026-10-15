$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Version = '0.6.0'
$SourceRoot = $PSScriptRoot
$ProjectFile = Join-Path $SourceRoot 'Valheim_Workers.csproj'
$DevRoot = Join-Path $env:USERPROFILE 'Documents\Valheim-Symbiosis-Dev'
$ProjectTarget = Join-Path $DevRoot 'WorkersCoreSource'
$DefaultValheim = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
$ThunderstoreRoot = Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'

function Write-Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Resolve-ValheimPath {
    if (Test-Path (Join-Path $DefaultValheim 'valheim.exe')) { return $DefaultValheim }

    $steamRoots = @(
        'C:\Program Files (x86)\Steam',
        'C:\Program Files\Steam'
    ) | Where-Object { Test-Path $_ }

    foreach ($steamRoot in $steamRoots) {
        $candidate = Join-Path $steamRoot 'steamapps\common\Valheim'
        if (Test-Path (Join-Path $candidate 'valheim.exe')) { return $candidate }
    }

    throw 'Valheim wurde nicht gefunden. Erwartet wurde eine normale Steam-Installation.'
}

function Resolve-BepInExPath {
    $preferred = Join-Path $ThunderstoreRoot 'Default\BepInEx'
    if (Test-Path (Join-Path $preferred 'core\BepInEx.dll')) { return $preferred }

    if (Test-Path $ThunderstoreRoot) {
        $matches = Get-ChildItem $ThunderstoreRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'BepInEx' } |
            Where-Object { Test-Path (Join-Path $_ 'core\BepInEx.dll') }
        $match = $matches | Select-Object -First 1
        if ($match) { return $match }
    }

    throw 'Kein aktives Thunderstore/BepInEx-Profil gefunden.'
}

Write-Host "Valheim Workers Core v$Version - v0.6.0 Release Candidate 1 Setup" -ForegroundColor Green

while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte das Spiel schliessen; Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

$ValheimPath = Resolve-ValheimPath
$ManagedPath = Join-Path $ValheimPath 'valheim_Data\Managed'
$BepInExPath = Resolve-BepInExPath
$PluginDir = Join-Path $BepInExPath 'plugins\Valheim_Workers'

if (-not (Test-Path $ProjectFile)) { throw "Projektdatei fehlt: $ProjectFile" }
if (-not (Test-Path (Join-Path $ManagedPath 'assembly_valheim.dll'))) { throw "Valheim Managed-Referenzen fehlen: $ManagedPath" }

Write-Step "Baue Workers Core v$Version"
& dotnet build $ProjectFile -c Release --nologo /p:BepInExPath="$BepInExPath" /p:ValheimManagedPath="$ManagedPath"
if ($LASTEXITCODE -ne 0) {
    throw "Core dotnet build fehlgeschlagen (ExitCode $LASTEXITCODE). Bestehende Installation bleibt unveraendert."
}

$BuiltDll = Join-Path $SourceRoot 'bin\Release\netstandard2.1\Valheim_Workers.dll'
if (-not (Test-Path $BuiltDll)) { throw "Build war erfolgreich, aber DLL wurde nicht gefunden: $BuiltDll" }

Write-Step 'Build erfolgreich - sichere Source und ersetze nur Workers Core'

New-Item -ItemType Directory -Path $DevRoot -Force | Out-Null
if (Test-Path $ProjectTarget) { Remove-Item $ProjectTarget -Recurse -Force }
New-Item -ItemType Directory -Path $ProjectTarget -Force | Out-Null

Get-ChildItem $SourceRoot -Force | Where-Object { $_.Name -notin @('bin','obj') } | ForEach-Object {
    Copy-Item $_.FullName -Destination $ProjectTarget -Recurse -Force
}

New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
$CoreDll = Join-Path $PluginDir 'Valheim_Workers.dll'

# Core-only update: current Mount, DrakeForm, Ego and the separate Updater are deliberately untouched.
if (Test-Path $CoreDll) {
    $backup = Join-Path $PluginDir 'Valheim_Workers.dll.pre-v0.6.0-rc1.bak'
    Copy-Item $CoreDll $backup -Force
}
Copy-Item $BuiltDll $CoreDll -Force

Write-Host "`nFERTIG: Valheim Workers Core v$Version wurde gebaut und installiert." -ForegroundColor Green
Write-Host "Source: $ProjectTarget"
Write-Host "Core:   $CoreDll"
Write-Host 'Mount / DrakeForm / Ego / Updater wurden NICHT veraendert.' -ForegroundColor DarkGray
Write-Host 'Test: Greydwarf-Nest, Draugr-Leichenhaufen und Surtling-FireHole testen; Spawner-Discovery laeuft jetzt ueber einen gemeinsamen Lifecycle-Index.' -ForegroundColor Yellow
