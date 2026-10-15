$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$SourceRoot = Split-Path -Parent $Root
$SuiteSetup = Join-Path $SourceRoot 'Setup.ps1'
[xml]$MountProject = Get-Content (Join-Path $SourceRoot 'src\Mount\Valheim_Workers_Mount.csproj') -Raw
$SuiteVersion = ($MountProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($SuiteVersion)) { throw 'Suite-Version konnte nicht gelesen werden.' }
$ReleaseName = "Valheim_Symbiosis_v${SuiteVersion}_Standalone_Installer"
$Downloads = Join-Path $env:USERPROFILE 'Downloads'
$Stage = Join-Path $env:TEMP $ReleaseName
$OutZip = Join-Path $Downloads ($ReleaseName + '.zip')

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Find-FileInPlugins([string]$BepInExPath, [string[]]$Names) {
    $plugins = Join-Path $BepInExPath 'plugins'
    if (-not (Test-Path $plugins)) { return $null }
    foreach ($name in $Names) {
        $f = Get-ChildItem $plugins -Recurse -File -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    return $null
}
function Get-BepInExProfiles {
    $roots = @(
        (Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'),
        (Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles')
    ) | Select-Object -Unique
    $out = @()
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $out += Get-ChildItem $r -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'BepInEx' } | Where-Object { Test-Path (Join-Path $_ 'core\BepInEx.dll') }
    }
    return @($out | Select-Object -Unique)
}

if (-not (Test-Path $SuiteSetup)) { throw "Suite Setup fehlt: $SuiteSetup" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK wurde nicht gefunden. Der Release-Builder braucht dotnet nur zum Erstellen der fertigen DLLs; Empfaenger des fertigen Installer-ZIPs brauchen dotnet NICHT.' }

Step 'Baue und installiere die aktuelle Symbiosis-Suite auf diesem PC'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $SuiteSetup
if ($LASTEXITCODE -ne 0) { throw "Suite-Build fehlgeschlagen (ExitCode $LASTEXITCODE)." }

$profiles = @(Get-BepInExProfiles)
if ($profiles.Count -eq 0) { throw 'Kein Thunderstore/r2modman BepInEx-Profil gefunden.' }

$selected = $null
foreach ($bep in $profiles) {
    $core = Find-FileInPlugins $bep @('Valheim_Workers.dll')
    $mount = Find-FileInPlugins $bep @('Valheim_Workers_Mount.dll')
    $form = Find-FileInPlugins $bep @('Valheim_Workers_DrakeForm.dll')
    $ego = Find-FileInPlugins $bep @('Valheim_Ego.dll','ValheimEgo.dll')
    $updater = Find-FileInPlugins $bep @('Valheim_Symbiosis_Updater.dll')
    if ($core -and $mount -and $form -and $ego -and $updater) {
        $selected = [pscustomobject]@{ BepInEx=$bep; Core=$core; Mount=$mount; Form=$form; Ego=$ego; Updater=$updater }
        break
    }
}
if (-not $selected) { throw 'Nach dem Build konnte kein Profil mit allen eigenen DLLs inklusive Updater gefunden werden.' }

$jotunn = Find-FileInPlugins $selected.BepInEx @('Jotunn.dll')
if (-not $jotunn) { Write-Warning 'Jotunn wurde im Quellprofil nicht gefunden. Das fertige Installer-ZIP wird Jotunn weiterhin als externe Voraussetzung pruefen.' }

Step 'Erzeuge eigenstaendiges Spieler-Installer-Paket'
Remove-Item $Stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $Stage 'Payload') -Force | Out-Null
Copy-Item $selected.Core  (Join-Path $Stage 'Payload\Valheim_Workers.dll') -Force
Copy-Item $selected.Mount (Join-Path $Stage 'Payload\Valheim_Workers_Mount.dll') -Force
Copy-Item $selected.Form  (Join-Path $Stage 'Payload\Valheim_Workers_DrakeForm.dll') -Force
Copy-Item $selected.Ego   (Join-Path $Stage 'Payload\Valheim_Ego.dll') -Force
Copy-Item $selected.Updater (Join-Path $Stage 'Payload\Valheim_Symbiosis_Updater.dll') -Force
$releaseCfg = Join-Path $env:APPDATA 'ValheimSymbiosisRelease\config.json'
if (Test-Path $releaseCfg) {
    $rc = Get-Content $releaseCfg -Raw | ConvertFrom-Json
    [ordered]@{ enabled=$true; owner=$rc.owner; repository=$rc.repository; channel='stable' } | ConvertTo-Json | Set-Content (Join-Path $Stage 'Payload\Valheim_Symbiosis_Update.json') -Encoding UTF8
} else {
    [ordered]@{ enabled=$false; owner=''; repository=''; channel='stable' } | ConvertTo-Json | Set-Content (Join-Path $Stage 'Payload\Valheim_Symbiosis_Update.json') -Encoding UTF8
}

$install = @'
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = $PSScriptRoot
$Payload = Join-Path $Root 'Payload'

function Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Get-Profiles {
    $roots = @(
        (Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'),
        (Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles')
    ) | Select-Object -Unique
    $out = @()
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $out += Get-ChildItem $r -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'BepInEx' } | Where-Object { Test-Path (Join-Path $_ 'core\BepInEx.dll') }
    }
    return @($out | Select-Object -Unique)
}
function Has-Jotunn([string]$BepInEx) {
    $p = Join-Path $BepInEx 'plugins'
    return $null -ne (Get-ChildItem $p -Recurse -File -Filter 'Jotunn.dll' -ErrorAction SilentlyContinue | Select-Object -First 1)
}
function Remove-OldOwnDlls([string]$Plugins) {
    $names = @('Valheim_Workers.dll','Valheim_Workers_Mount.dll','Valheim_Workers_DrakeForm.dll','Valheim_Ego.dll','ValheimEgo.dll','Valheim_Symbiosis_Updater.dll')
    foreach ($n in $names) {
        Get-ChildItem $Plugins -Recurse -File -Filter $n -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

foreach ($f in @('Valheim_Workers.dll','Valheim_Workers_Mount.dll','Valheim_Workers_DrakeForm.dll','Valheim_Ego.dll','Valheim_Symbiosis_Updater.dll','Valheim_Symbiosis_Update.json')) {
    if (-not (Test-Path (Join-Path $Payload $f))) { throw "Payload fehlt: $f" }
}
while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte Spiel schliessen; Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}
$profiles = @(Get-Profiles)
if ($profiles.Count -eq 0) { throw 'Kein Thunderstore/r2modman-Profil mit BepInEx gefunden. Bitte BepInEx/Thunderstore zuerst installieren.' }
$targets = @($profiles | Where-Object { Has-Jotunn $_ })
if ($targets.Count -eq 0) { throw 'Jotunn fehlt. Bitte Jotunn in mindestens einem Valheim-Profil installieren und den Installer erneut starten.' }

foreach ($bep in $targets) {
    $plugins = Join-Path $bep 'plugins'
    $dest = Join-Path $plugins 'Valheim_Symbiosis'
    Step "Installiere in $bep"
    Remove-OldOwnDlls $plugins
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item (Join-Path $Payload '*.dll') $dest -Force
    Copy-Item (Join-Path $Payload 'Valheim_Symbiosis_Update.json') $dest -Force
}
Write-Host "`nFERTIG: Valheim Symbiosis + Workers Core in $($targets.Count) Profil(en) installiert." -ForegroundColor Green
Write-Host 'Externe Voraussetzung: Jotunn (und der Mod-Loader BepInEx/Thunderstore).' -ForegroundColor Yellow
'@
Set-Content (Join-Path $Stage 'Install.ps1') $install -Encoding UTF8

$uninstall = @'
$ErrorActionPreference='Stop'
$roots=@((Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'),(Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles')) | Select-Object -Unique
$names=@('Valheim_Workers.dll','Valheim_Workers_Mount.dll','Valheim_Workers_DrakeForm.dll','Valheim_Ego.dll','ValheimEgo.dll','Valheim_Symbiosis_Updater.dll')
foreach($r in $roots){ if(!(Test-Path $r)){continue}; foreach($p in Get-ChildItem $r -Directory -EA 0){$plugins=Join-Path $p.FullName 'BepInEx\plugins'; if(!(Test-Path $plugins)){continue}; foreach($n in $names){Get-ChildItem $plugins -Recurse -File -Filter $n -EA 0|Remove-Item -Force -EA 0}; Remove-Item (Join-Path $plugins 'Valheim_Symbiosis') -Recurse -Force -EA 0}}
Write-Host 'Valheim Symbiosis / Workers Core entfernt. Jotunn und BepInEx wurden nicht veraendert.' -ForegroundColor Green
'@
Set-Content (Join-Path $Stage 'Uninstall.ps1') $uninstall -Encoding UTF8

$bat = '@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"\r\npause\r\n'
[System.IO.File]::WriteAllText((Join-Path $Stage 'Install.bat'), $bat.Replace('\\r\\n',"`r`n"), [System.Text.Encoding]::ASCII)
$ubat = '@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"\r\npause\r\n'
[System.IO.File]::WriteAllText((Join-Path $Stage 'Uninstall.bat'), $ubat.Replace('\\r\\n',"`r`n"), [System.Text.Encoding]::ASCII)

$readme = @"
VALHEIM SYMBIOSIS - STANDALONE INSTALLER
========================================
Enthalten:
- Valheim_Workers.dll (Workers Core)
- Valheim_Workers_Mount.dll
- Valheim_Workers_DrakeForm.dll
- Valheim_Ego.dll
- Valheim_Symbiosis_Updater.dll (automatische GitHub-Updates)

Voraussetzungen auf dem Ziel-PC:
- Valheim
- BepInEx / Thunderstore oder r2modman
- Jotunn

Installation:
1. ZIP entpacken.
2. Install.bat doppelklicken.
   Alternativ: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
3. Der Installer installiert in alle gefundenen Valheim-Profile, in denen Jotunn vorhanden ist.

Deinstallation:
- Uninstall.bat doppelklicken.

Hinweis:
- Vorhandene BepInEx/Jotunn-Dateien werden NICHT veraendert.
- Alte eigene Symbiosis/Workers-DLLs werden vor der Installation entfernt.
- Konfigurationsdateien unter BepInEx\config bleiben erhalten.
"@
Set-Content (Join-Path $Stage 'README.txt') $readme -Encoding UTF8

$manifest = [ordered]@{
    package='Valheim Symbiosis Standalone Installer'
    suite=$SuiteVersion
    mount=$SuiteVersion
    drakeForm='0.7.0'
    ego='1.6.49'
    workersCore='bundled from local installed build'
    external=@('BepInEx','Jotunn')
    built=(Get-Date).ToString('s')
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Stage 'manifest.json') -Encoding UTF8

Remove-Item $OutZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $OutZip -CompressionLevel Optimal
Write-Host "`nFERTIGES TEILBARES INSTALLER-ZIP:" -ForegroundColor Green
Write-Host $OutZip -ForegroundColor White
Write-Host 'Empfaenger brauchen nur BepInEx/Thunderstore + Jotunn. dotnet/Sourcecode wird NICHT benoetigt.' -ForegroundColor Yellow
