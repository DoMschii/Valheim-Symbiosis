$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Version = '4.5.2'
$SourceRoot = $PSScriptRoot
$ProjectFile = Join-Path $SourceRoot 'Valheim_Workers_Mount.csproj'
$ProjectTarget = 'C:\App Dev\Projekte\Valheim_Workers_Mount'
$DefaultValheim = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
$ThunderstoreRoot = Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'

function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Resolve-ValheimPath {
    if (Test-Path (Join-Path $DefaultValheim 'valheim.exe')) { return $DefaultValheim }
    foreach ($steamRoot in @('C:\Program Files (x86)\Steam','C:\Program Files\Steam')) {
        if (-not (Test-Path $steamRoot)) { continue }
        $candidate = Join-Path $steamRoot 'steamapps\common\Valheim'
        if (Test-Path (Join-Path $candidate 'valheim.exe')) { return $candidate }
    }
    throw 'Valheim wurde nicht gefunden.'
}

function Resolve-BepInExTargets {
    $targets = @()
    if (Test-Path $ThunderstoreRoot) {
        $targets = @(Get-ChildItem $ThunderstoreRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'BepInEx' } |
            Where-Object {
                (Test-Path (Join-Path $_ 'core\BepInEx.dll')) -and
                (Test-Path (Join-Path $_ 'plugins\Valheim_Workers\Valheim_Workers.dll'))
            })
    }
    if (-not $targets -or $targets.Count -eq 0) { throw 'Kein Thunderstore/BepInEx-Profil mit installiertem Valheim_Workers Core gefunden.' }
    return @($targets)
}

Write-Host "Valheim Workers Mount v$Version - OneClick Setup" -ForegroundColor Green
while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte das Spiel schliessen; Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

$ValheimPath = Resolve-ValheimPath
$ManagedPath = Join-Path $ValheimPath 'valheim_Data\Managed'
$BepInExTargets = @(Resolve-BepInExTargets)
$BepInExPath = $BepInExTargets[0]
$PluginDir = Join-Path $BepInExPath 'plugins\Valheim_Workers'
$CoreDll = Join-Path $PluginDir 'Valheim_Workers.dll'

if (-not (Test-Path $CoreDll)) { throw 'Valheim_Workers.dll fehlt. Installiere zuerst Valheim_Workers v0.5.436 oder neuer.' }
$coreVersion = [System.Reflection.AssemblyName]::GetAssemblyName($CoreDll).Version
if ($coreVersion -lt [Version]'0.5.436') { throw "Workers Core ist zu alt ($coreVersion). Benoetigt wird mindestens 0.5.436." }
if ($coreVersion -lt [Version]'0.5.118') { Write-Host "HINWEIS: Core $coreVersion nutzt noch die alte Bridge. Mount baut trotzdem; der Drake-Mount-Tab benoetigt Core v0.5.118+." -ForegroundColor Yellow }
if (-not (Test-Path $ProjectFile)) { throw "Projektdatei fehlt: $ProjectFile" }

Write-Step "Baue Mount v$Version gegen Workers Core $coreVersion"
& dotnet build $ProjectFile -c Release --nologo /p:BepInExPath="$BepInExPath" /p:ValheimManagedPath="$ManagedPath" /p:WorkersCorePath="$CoreDll"
if ($LASTEXITCODE -ne 0) { throw "dotnet build fehlgeschlagen (ExitCode $LASTEXITCODE). Bestehende Installation bleibt unveraendert." }

$BuiltDll = Join-Path $SourceRoot 'bin\Release\netstandard2.1\Valheim_Workers_Mount.dll'
if (-not (Test-Path $BuiltDll)) { throw "Build war erfolgreich, aber DLL fehlt: $BuiltDll" }

Write-Step 'Build erfolgreich - installiere nur das Mount-Add-on'
if (Test-Path $ProjectTarget) { Remove-Item $ProjectTarget -Recurse -Force }
New-Item -ItemType Directory -Path $ProjectTarget -Force | Out-Null
Get-ChildItem $SourceRoot -Force | Where-Object { $_.Name -notin @('bin','obj') } | ForEach-Object {
    Copy-Item $_.FullName -Destination $ProjectTarget -Recurse -Force
}

$InstalledMounts = @()
foreach ($targetBepInEx in $BepInExTargets) {
    $targetPluginDir = Join-Path $targetBepInEx 'plugins\Valheim_Workers'
    New-Item -ItemType Directory -Path $targetPluginDir -Force | Out-Null
    $MountDll = Join-Path $targetPluginDir 'Valheim_Workers_Mount.dll'
    if (Test-Path $MountDll) { Remove-Item $MountDll -Force }
    Copy-Item $BuiltDll $MountDll -Force
    $installedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($MountDll).Version
    if ($installedVersion -lt [Version]'4.5.2') { throw "Installierte Mount-DLL ist unerwartet alt: $MountDll => $installedVersion" }
    $InstalledMounts += $MountDll
    Write-Host "Installiert in Profil: $targetBepInEx => Mount $installedVersion" -ForegroundColor Green
}

$Downloads = Join-Path $env:USERPROFILE 'Downloads'
Get-ChildItem $Downloads -Filter 'Valheim_Workers_Mount_v*.zip' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "Valheim_Workers_Mount_v${Version}_*" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "`nFERTIG: Valheim Workers Mount v$Version wurde gebaut und installiert." -ForegroundColor Green
Write-Host "Core bleibt unveraendert: $CoreDll"
Write-Host "Mount-Ziele: $($InstalledMounts -join ' ; ')"
Write-Host "Projekt: $ProjectTarget"
Write-Host 'Neue DLL installiert: Valheim jetzt neu starten.' -ForegroundColor Yellow
