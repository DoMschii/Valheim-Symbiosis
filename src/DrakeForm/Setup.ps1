$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Version = '0.7.0'
$SourceRoot = $PSScriptRoot
$ProjectFile = Join-Path $SourceRoot 'Valheim_Workers_DrakeForm.csproj'
$ProjectTarget = 'C:\App Dev\Projekte\Valheim_Workers_DrakeForm'
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

Write-Host "Valheim Workers DrakeForm v$Version - OneClick Setup" -ForegroundColor Green
while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte das Spiel schliessen; Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

$ValheimPath = Resolve-ValheimPath
$ManagedPath = Join-Path $ValheimPath 'valheim_Data\Managed'
$BepInExTargets = @(Resolve-BepInExTargets)
$BepInExPath = $BepInExTargets[0]
$PluginDir = Join-Path $BepInExPath 'plugins\Valheim_Workers'
if (-not (Test-Path $ProjectFile)) { throw "Projektdatei fehlt: $ProjectFile" }

Write-Step "Baue DrakeForm v$Version"
& dotnet build $ProjectFile -c Release --nologo /p:BepInExPath="$BepInExPath" /p:ValheimManagedPath="$ManagedPath"
if ($LASTEXITCODE -ne 0) { throw "dotnet build fehlgeschlagen (ExitCode $LASTEXITCODE). Bestehende Installation bleibt unveraendert." }

$BuiltDll = Join-Path $SourceRoot 'bin\Release\netstandard2.1\Valheim_Workers_DrakeForm.dll'
if (-not (Test-Path $BuiltDll)) { throw "Build war erfolgreich, aber DLL fehlt: $BuiltDll" }

Write-Step 'Build erfolgreich - installiere DrakeForm'
if (Test-Path $ProjectTarget) { Remove-Item $ProjectTarget -Recurse -Force }
New-Item -ItemType Directory -Path $ProjectTarget -Force | Out-Null
Get-ChildItem $SourceRoot -Force | Where-Object { $_.Name -notin @('bin','obj') } | ForEach-Object {
    Copy-Item $_.FullName -Destination $ProjectTarget -Recurse -Force
}
$InstalledDrakeForms = @()
foreach ($targetBepInEx in $BepInExTargets) {
    $targetPluginDir = Join-Path $targetBepInEx 'plugins\Valheim_Workers'
    New-Item -ItemType Directory -Path $targetPluginDir -Force | Out-Null
    $Dll = Join-Path $targetPluginDir 'Valheim_Workers_DrakeForm.dll'
    if (Test-Path $Dll) { Remove-Item $Dll -Force }
    Copy-Item $BuiltDll $Dll -Force
    $installedVersion = [System.Reflection.AssemblyName]::GetAssemblyName($Dll).Version
    if ($installedVersion -lt [Version]$Version) { throw "Installierte DrakeForm-DLL ist unerwartet alt: $Dll => $installedVersion" }
    $InstalledDrakeForms += $Dll
    Write-Host "Installiert in Profil: $targetBepInEx => DrakeForm $installedVersion" -ForegroundColor Green
}
Write-Host "`nFERTIG: Valheim Workers DrakeForm v$Version wurde gebaut und in $($InstalledDrakeForms.Count) Profil(en) installiert." -ForegroundColor Green
Write-Host "DrakeForm-Ziele: $($InstalledDrakeForms -join ' ; ')"
