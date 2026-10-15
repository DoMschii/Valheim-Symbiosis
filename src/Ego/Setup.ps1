$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PatchVersion = '1.6.50'
$Root = $PSScriptRoot
$PatchFile = Join-Path $Root 'EgoFacingLockPatch.cs'
$ProjectRoot = 'C:\App Dev\Projekte\Valheim_Ego'
$Project = Join-Path $ProjectRoot 'Valheim_Ego.csproj'
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

function XmlEscape([string]$Value) {
    if ($null -eq $Value) { return '' }
    return [System.Security.SecurityElement]::Escape($Value)
}


function Remove-OldFacingLockZips {
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    if (-not (Test-Path $downloads)) { return }
    Get-ChildItem $downloads -File -Filter 'Valheim_Ego_v*.zip' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch ('^Valheim_Ego_v' + [regex]::Escape($PatchVersion) + '(_|\.)') } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "Valheim Ego v$PatchVersion - Ego + Symbiosis Vanilla Third-Person Control" -ForegroundColor Green
Remove-OldFacingLockZips
while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte das Spiel schliessen; Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $Project)) {
    throw "Valheim-Ego-Quellprojekt nicht gefunden: $Project"
}
if (-not (Test-Path $PatchFile)) { throw "Patch-Quelldatei fehlt: $PatchFile" }

$ValheimPath = Resolve-ValheimPath
$ManagedPath = Join-Path $ValheimPath 'valheim_Data\Managed'
$BepInExTargets = @(Resolve-BepInExTargets)
$BepInExPath = $BepInExTargets[0]
$CorePath = Join-Path $BepInExPath 'core'
$PluginsPath = Join-Path $BepInExPath 'plugins'

foreach ($required in @(
    (Join-Path $ManagedPath 'assembly_valheim.dll'),
    (Join-Path $ManagedPath 'UnityEngine.CoreModule.dll'),
    (Join-Path $CorePath 'BepInEx.dll'),
    (Join-Path $CorePath '0Harmony.dll')
)) {
    if (-not (Test-Path $required)) { throw "Build-Abhaengigkeit fehlt: $required" }
}

# Build completely outside the user's source tree first.  v1.6.31 failed because
# the existing csproj's HintPaths did not point at the active Valheim/BepInEx
# installation.  This staged project supplies explicit, resolved references.
$Stage = Join-Path $env:TEMP 'Valheim_Ego_v1.6.50_Build'
Remove-Item $Stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Stage -Force | Out-Null

Write-Step 'Stelle Ego-Quellen transaktional im Temp-Ordner zusammen'
$sourceFiles = Get-ChildItem $ProjectRoot -Recurse -File -Filter '*.cs' -ErrorAction Stop |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|_FacingFix_Backup)[\\/]' -and
        $_.Name -ne 'EgoFacingLockPatch.cs'
    }

if (-not ($sourceFiles | Where-Object { $_.Name -eq 'FirstPersonPlugin.cs' })) {
    throw 'FirstPersonPlugin.cs wurde im Valheim_Ego-Projekt nicht gefunden.'
}

foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($ProjectRoot.Length).TrimStart([char[]]@([char]92,[char]47))
    $dest = Join-Path $Stage $relative
    $destDir = Split-Path $dest -Parent
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item $file.FullName $dest -Force
}
Copy-Item $PatchFile (Join-Path $Stage 'EgoFacingLockPatch.cs') -Force

# Version only in staged sources.  User source is changed only after a successful build.
$stagedPlugin = Get-ChildItem $Stage -Recurse -File -Filter 'FirstPersonPlugin.cs' | Select-Object -First 1
$src = Get-Content $stagedPlugin.FullName -Raw
$src = $src -replace '1\.6\.(30|31|32|33|34|35|36|37|38|39|40|41|42|43|44|45|46|47|48|49)', $PatchVersion
Set-Content -Path $stagedPlugin.FullName -Value $src -Encoding UTF8

# Build against .NET Framework 4.8.  Some optional Ego integrations in the
# active profile (for example Digitalroot.HeightmapUnlimitedJvL) are built for
# net48.  net472 caused MSB3274 and is needlessly restrictive on this setup.
$targetFramework = 'net48'

# Build references are intentionally limited to Valheim Managed + BepInEx/core.
# v1.6.35 still scanned the complete plugin tree. MSBuild then resolved a legacy
# 0Harmony20 dependency from another mod and both Harmony implementations reached
# the compiler. Ego itself only needs the game/Unity assemblies plus BepInEx core.
$canonicalHarmony = Join-Path $CorePath '0Harmony.dll'
$allDlls = @()
$allDlls += Get-ChildItem $ManagedPath -File -Filter '*.dll' -ErrorAction Stop
$allDlls += Get-ChildItem $CorePath -File -Filter '*.dll' -ErrorAction Stop

function Get-AssemblyIdentityName([System.IO.FileInfo]$Dll) {
    try {
        return [System.Reflection.AssemblyName]::GetAssemblyName($Dll.FullName).Name
    } catch {
        return $Dll.BaseName
    }
}

$refsByIdentity = @{}
$ignoredLegacyHarmony = New-Object System.Collections.Generic.List[string]
foreach ($dll in $allDlls) {
    $identity = Get-AssemblyIdentityName $dll
    if ([string]::IsNullOrWhiteSpace($identity)) { $identity = $dll.BaseName }
    $key = $identity.ToLowerInvariant()
    $baseKey = $dll.BaseName.ToLowerInvariant()

    # Never allow Harmony 2.0 legacy assemblies into the compiler, regardless of
    # filename OR assembly identity. This catches 0Harmony20 even when the file is
    # discovered transitively under a non-obvious name.
    if ($key -in @('0harmony20','harmony20') -or $baseKey -in @('0harmony20','harmony20')) {
        [void]$ignoredLegacyHarmony.Add($dll.FullName)
        continue
    }

    # The only accepted HarmonyLib provider is BepInEx/core/0Harmony.dll.
    if ($key -eq '0harmony' -or $baseKey -eq '0harmony') {
        if ($dll.FullName -ieq $canonicalHarmony) {
            $refsByIdentity['0harmony'] = $canonicalHarmony
        }
        continue
    }

    # Defensive: never compile against an already installed Ego assembly.
    if ($key -in @('valheim_ego','valheimego') -or $baseKey -in @('valheim_ego','valheimego')) {
        continue
    }

    if (-not $refsByIdentity.ContainsKey($key)) {
        $refsByIdentity[$key] = $dll.FullName
    }
}

# Force canonical Harmony even if enumeration order changes.
$refsByIdentity['0harmony'] = $canonicalHarmony

if ($ignoredLegacyHarmony.Count -gt 0) {
    Write-Host 'Ignoriere Legacy-Harmony-Assembly(s):' -ForegroundColor DarkGray
    $ignoredLegacyHarmony | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}
Write-Host "Build-Referenzen: Valheim Managed + BepInEx/core ($($refsByIdentity.Count) eindeutige Assemblies); Plugins werden NICHT referenziert." -ForegroundColor DarkGray

$references = New-Object System.Text.StringBuilder
foreach ($key in ($refsByIdentity.Keys | Sort-Object)) {
    $path = XmlEscape $refsByIdentity[$key]
    $name = XmlEscape ([System.IO.Path]::GetFileNameWithoutExtension($refsByIdentity[$key]))
    [void]$references.AppendLine(('    <Reference Include="{0}"><HintPath>{1}</HintPath><Private>false</Private><ExternallyResolved>true</ExternallyResolved></Reference>' -f $name, $path))
}

$buildProject = Join-Path $Stage 'Valheim_Ego.Build.csproj'
$assemblyName = 'Valheim_Ego'
$projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$targetFramework</TargetFramework>
    <AssemblyName>$assemblyName</AssemblyName>
    <RootNamespace>Valheim_Ego</RootNamespace>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <Deterministic>false</Deterministic>
    <FindDependenciesOfExternallyResolvedReferences>false</FindDependenciesOfExternallyResolvedReferences>
  </PropertyGroup>
  <ItemGroup>
$($references.ToString())  </ItemGroup>
</Project>
"@
Set-Content -Path $buildProject -Value $projectXml -Encoding UTF8

Write-Step "Baue Valheim Ego v$PatchVersion mit aufgeloesten Spiel-/BepInEx-Referenzen"
& dotnet build $buildProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Valheim Ego Build fehlgeschlagen (ExitCode $LASTEXITCODE). Installierte DLL UND bestehende Projektquellen wurden nicht ersetzt."
}

$built = Get-ChildItem (Join-Path $Stage 'bin\Release') -Recurse -File -Filter 'Valheim_Ego.dll' -ErrorAction Stop |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $built) { throw 'Build war erfolgreich, aber Valheim_Ego.dll wurde nicht gefunden.' }

Write-Step 'Uebernehme erfolgreichen Patch in das Ego-Quellprojekt'
$backupDir = Join-Path $ProjectRoot '_FacingFix_Backup'
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$targetPatch = Join-Path $ProjectRoot 'EgoFacingLockPatch.cs'
if (Test-Path $targetPatch) {
    Copy-Item $targetPatch (Join-Path $backupDir ('EgoFacingLockPatch_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.cs')) -Force
}
Copy-Item $PatchFile $targetPatch -Force

$pluginSource = Join-Path $ProjectRoot 'FirstPersonPlugin.cs'
if (Test-Path $pluginSource) {
    Copy-Item $pluginSource (Join-Path $backupDir ('FirstPersonPlugin_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.cs')) -Force
    $projectSrc = Get-Content $pluginSource -Raw
    $projectSrc = $projectSrc -replace '1\.6\.(30|31|32|33|34|35|36|37|38|39|40|41|42|43|44|45|46|47|48|49)', $PatchVersion
    Set-Content -Path $pluginSource -Value $projectSrc -Encoding UTF8
}

$InstalledEgo = @()
foreach ($targetBepInEx in $BepInExTargets) {
    $targetPluginsPath = Join-Path $targetBepInEx 'plugins'
    $installed = Get-ChildItem $targetPluginsPath -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Valheim[_ ]?Ego\.dll$|^ValheimEgo\.dll$' } |
        Select-Object -First 1
    if ($installed) {
        $dest = $installed.FullName
    } else {
        $destDir = Join-Path $targetPluginsPath 'Valheim_Ego'
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        $dest = Join-Path $destDir 'Valheim_Ego.dll'
    }

    Write-Step "Installiere aktualisierte Ego-DLL in $targetBepInEx"
    Copy-Item $built.FullName $dest -Force
    $InstalledEgo += $dest
}

Write-Step 'Raeume alte Ego-FacingLock-ZIPs auf'
Remove-OldFacingLockZips

Write-Host "`nFERTIG: Valheim Ego Facing Fix v$PatchVersion in $($InstalledEgo.Count) Profil(en) installiert." -ForegroundColor Green
Write-Host "Ego-Ziele: $($InstalledEgo -join ' ; ')"
Write-Host 'Ego: Blickrichtung bleibt Steuerbasis; S rueckwaerts, A/D strafe.' -ForegroundColor Yellow
Write-Host 'Third Person: Werwolf/Serpent nutzt Vanilla-Prinzip: Kamera frei, Koerper/Blick folgt der Laufrichtung; normaler Charakter bleibt vanilla.' -ForegroundColor Yellow
