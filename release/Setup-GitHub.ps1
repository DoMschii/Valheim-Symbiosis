$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$ConfigDir=Join-Path $env:APPDATA 'ValheimSymbiosisRelease'
$ConfigFile=Join-Path $ConfigDir 'config.json'
$ToolsDir=Join-Path $PSScriptRoot 'tools\gh'
function Step($t){ Write-Host "`n==> $t" -ForegroundColor Cyan }
function Resolve-GhExe {
  $cmd=Get-Command gh -EA 0
  if($cmd){ return $cmd.Source }
  $known=@(
    (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'),
    (Join-Path $ToolsDir 'bin\gh.exe')
  ) | Where-Object { $_ -and (Test-Path $_) }
  if($known.Count -gt 0){ return $known[0] }
  return $null
}
function Refresh-ProcessPath {
  $machine=[Environment]::GetEnvironmentVariable('Path','Machine')
  $user=[Environment]::GetEnvironmentVariable('Path','User')
  $env:Path=(@($machine,$user) | Where-Object {$_}) -join ';'
}
function Install-PortableGh {
  Step 'GitHub CLI wird portabel geladen'
  New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
  $headers=@{'User-Agent'='Valheim-Symbiosis-ReleaseTool'}
  $release=Invoke-RestMethod -Uri 'https://api.github.com/repos/cli/cli/releases/latest' -Headers $headers
  $asset=$release.assets | Where-Object { $_.name -match '^gh_.*_windows_amd64\.zip$' } | Select-Object -First 1
  if(-not $asset){ throw 'Passendes GitHub-CLI Windows-x64 Paket wurde nicht gefunden.' }
  $zip=Join-Path $env:TEMP $asset.name
  Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zip -UseBasicParsing
  $extract=Join-Path $env:TEMP 'ValheimSymbiosis_gh'
  Remove-Item $extract -Recurse -Force -EA 0
  Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
  $exe=Get-ChildItem $extract -Filter gh.exe -Recurse | Select-Object -First 1
  if(-not $exe){ throw 'gh.exe konnte im heruntergeladenen Paket nicht gefunden werden.' }
  $bin=Join-Path $ToolsDir 'bin'
  New-Item -ItemType Directory -Path $bin -Force | Out-Null
  Copy-Item $exe.FullName (Join-Path $bin 'gh.exe') -Force
  return (Join-Path $bin 'gh.exe')
}

$gh=Resolve-GhExe
if(-not $gh){
  if(Get-Command winget -EA 0){
    Step 'GitHub CLI wird einmalig installiert'
    & winget install --id GitHub.cli --exact --accept-package-agreements --accept-source-agreements
    if($LASTEXITCODE -ne 0){ Write-Host 'winget konnte GitHub CLI nicht installieren. Nutze portable Fallback-Version.' -ForegroundColor Yellow }
    Refresh-ProcessPath
    $gh=Resolve-GhExe
  }
  if(-not $gh){ $gh=Install-PortableGh }
}
if(-not (Test-Path $gh)){ throw 'GitHub CLI konnte nicht bereitgestellt werden.' }
Write-Host "GitHub CLI: $gh" -ForegroundColor DarkGray

Step 'GitHub-Anmeldung pruefen'
$oldEap=$ErrorActionPreference
$ErrorActionPreference='Continue'
& $gh auth status *> $null
$authExit=$LASTEXITCODE
$ErrorActionPreference=$oldEap
if($authExit -ne 0){
  Write-Host 'Noch nicht bei GitHub angemeldet - Browser-Login wird gestartet.' -ForegroundColor Yellow
  & $gh auth login --web
  if($LASTEXITCODE -ne 0){ throw 'GitHub Login fehlgeschlagen.' }
}
$me = (& $gh api user --jq .login).Trim()
if([string]::IsNullOrWhiteSpace($me)){ throw 'GitHub Benutzer konnte nicht ermittelt werden.' }
$owner=Read-Host "GitHub Owner [$me]"; if([string]::IsNullOrWhiteSpace($owner)){$owner=$me}
$repo=Read-Host 'Repository [Valheim-Symbiosis]'; if([string]::IsNullOrWhiteSpace($repo)){$repo='Valheim-Symbiosis'}
$visibility=Read-Host 'Repository oeffentlich? (J/N) [J]'; $isPublic=($visibility -notmatch '^[Nn]')
$oldEap=$ErrorActionPreference
$ErrorActionPreference='Continue'
& $gh repo view "$owner/$repo" *> $null
$repoExit=$LASTEXITCODE
$ErrorActionPreference=$oldEap
if($repoExit -ne 0){
  Step "Erstelle GitHub Repository $owner/$repo"
  $vis = if($isPublic){'--public'}else{'--private'}
  & $gh repo create "$owner/$repo" $vis --description 'Valheim Symbiosis releases and automatic updates'
  if($LASTEXITCODE -ne 0){ throw 'Repository konnte nicht erstellt werden.' }
}
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
[ordered]@{owner=$owner;repository=$repo;channel='stable';gh=$gh} | ConvertTo-Json | Set-Content $ConfigFile -Encoding UTF8
Write-Host "`nFERTIG. Release-Konfiguration: $owner/$repo" -ForegroundColor Green
Write-Host "Gespeichert: $ConfigFile" -ForegroundColor DarkGray
