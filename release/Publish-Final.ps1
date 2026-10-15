param([string]$Notes='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$ConfigFile=Join-Path $env:APPDATA 'ValheimSymbiosisRelease\config.json'
if(!(Test-Path $ConfigFile)){ throw 'GitHub ist noch nicht eingerichtet. Einmal Setup-GitHub.bat starten.' }
$cfg=Get-Content $ConfigFile -Raw | ConvertFrom-Json
$sourceRoot=Split-Path -Parent $PSScriptRoot
$csproj=Join-Path $sourceRoot 'Mount\Valheim_Workers_Mount.csproj'
[xml]$proj=Get-Content $csproj -Raw
$version=($proj.Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1).Trim()
if([string]::IsNullOrWhiteSpace($version)){ throw 'Suite-Version konnte nicht gelesen werden.' }
$answer=Read-Host "Version v$version wirklich FINAL getestet und veroeffentlichen? (JA eingeben)"
if($answer -ne 'JA'){ Write-Host 'Abgebrochen.' -ForegroundColor Yellow; exit 0 }
$gh=$null
if($cfg.PSObject.Properties.Name -contains 'gh' -and $cfg.gh -and (Test-Path $cfg.gh)){ $gh=$cfg.gh }
if(-not $gh){ $cmd=Get-Command gh -EA 0; if($cmd){ $gh=$cmd.Source } }
if(-not $gh){ $known=Join-Path $PSScriptRoot 'tools\gh\bin\gh.exe'; if(Test-Path $known){ $gh=$known } }
if(-not $gh){ throw 'GitHub CLI fehlt. Setup-GitHub.bat erneut starten.' }
& $gh auth status *> $null
if($LASTEXITCODE -ne 0){ throw 'GitHub Login abgelaufen. Setup-GitHub.bat erneut starten.' }
Write-Host "`n==> Erzeuge Standalone-Installer" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-Standalone-Release.ps1')
if($LASTEXITCODE -ne 0){ throw 'Release Builder fehlgeschlagen.' }
$zip=Join-Path $env:USERPROFILE "Downloads\Valheim_Symbiosis_v${version}_Standalone_Installer.zip"
if(!(Test-Path $zip)){ throw "Release ZIP fehlt: $zip" }
$sha=((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash).ToLowerInvariant()
$shaFile="$zip.sha256"
"$sha  $([IO.Path]::GetFileName($zip))" | Set-Content $shaFile -Encoding ASCII
$tag="v$version"
$oldEap=$ErrorActionPreference
$ErrorActionPreference='Continue'
& $gh release view $tag --repo "$($cfg.owner)/$($cfg.repository)" *> $null
$releaseExit=$LASTEXITCODE
$ErrorActionPreference=$oldEap
if($releaseExit -eq 0){ throw "Release $tag existiert bereits. Version erhoehen statt Release zu ueberschreiben." }
if([string]::IsNullOrWhiteSpace($Notes)){$Notes="Valheim Symbiosis $tag - final tested release"}
$notesFile=Join-Path $env:TEMP 'Valheim_Symbiosis_release_notes.txt'
$Notes | Set-Content $notesFile -Encoding UTF8
Write-Host "`n==> Lade FINAL Release zu GitHub hoch" -ForegroundColor Cyan
& $gh release create $tag $zip $shaFile --repo "$($cfg.owner)/$($cfg.repository)" --title "Valheim Symbiosis $tag" --notes-file $notesFile
if($LASTEXITCODE -ne 0){ throw 'GitHub Release Upload fehlgeschlagen.' }
Write-Host "`nFERTIG: $tag ist online. Installierte Clients finden das Update beim naechsten Valheim-Start automatisch." -ForegroundColor Green
