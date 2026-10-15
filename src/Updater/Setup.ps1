$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Version='4.5.1'
$Root=$PSScriptRoot
$DefaultValheim='C:\Program Files (x86)\Steam\steamapps\common\Valheim'
$ProfileRoots=@(
  (Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'),
  (Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles')
) | Select-Object -Unique

function Resolve-ValheimPath {
  foreach($p in @($DefaultValheim,'C:\Program Files\Steam\steamapps\common\Valheim')){
    if(Test-Path (Join-Path $p 'valheim.exe')){ return $p }
  }
  throw 'Valheim wurde nicht gefunden.'
}
function Get-BepTargets {
  $list=@()
  foreach($r in $ProfileRoots){
    if(!(Test-Path $r)){continue}
    $list += @(Get-ChildItem $r -Directory -EA 0 | ForEach-Object { Join-Path $_.FullName 'BepInEx' } | Where-Object { Test-Path (Join-Path $_ 'core\BepInEx.dll') })
  }
  return @($list | Select-Object -Unique)
}
while(Get-Process -Name valheim -EA 0){ Write-Host 'Valheim laeuft noch. Installation wartet...' -ForegroundColor Yellow; Start-Sleep 2 }
$valheim=Resolve-ValheimPath
$managed=Join-Path $valheim 'valheim_Data\Managed'
$targets=@(Get-BepTargets)
if($targets.Count -eq 0){ throw 'Kein BepInEx/Thunderstore-Profil gefunden.' }
$bep=$targets[0]
$stage=Join-Path $env:TEMP 'Valheim_Symbiosis_Updater_Build'
Remove-Item $stage -Recurse -Force -EA 0
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $Root 'Plugin.cs') $stage -Force
Copy-Item (Join-Path $Root 'Valheim_Symbiosis_Updater.csproj') $stage -Force
$env:VALHEIM_BEPINEX=$bep
$env:VALHEIM_MANAGED=$managed
Write-Host "Baue Valheim Symbiosis Updater v$Version..." -ForegroundColor Cyan
& dotnet build (Join-Path $stage 'Valheim_Symbiosis_Updater.csproj') -c Release -v minimal
if($LASTEXITCODE -ne 0){ throw "Updater build failed ($LASTEXITCODE)" }
$dll=Join-Path $stage 'bin\Release\netstandard2.1\Valheim_Symbiosis_Updater.dll'
if(!(Test-Path $dll)){ throw 'Updater DLL wurde nicht erzeugt.' }
foreach($t in $targets){
  $dest=Join-Path $t 'plugins\Valheim_Symbiosis'
  New-Item -ItemType Directory -Path $dest -Force | Out-Null
  Copy-Item $dll (Join-Path $dest 'Valheim_Symbiosis_Updater.dll') -Force
}
Write-Host "Updater v$Version installiert. Ohne GitHub-Konfiguration bleibt er automatisch inaktiv." -ForegroundColor Green
