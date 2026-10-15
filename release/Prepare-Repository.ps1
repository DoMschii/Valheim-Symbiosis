$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$ConfigFile=Join-Path $env:APPDATA 'ValheimSymbiosisRelease\config.json'
if(!(Test-Path $ConfigFile)){ throw 'GitHub ist noch nicht eingerichtet. Erst release\Setup-GitHub.bat starten.' }
$cfg=Get-Content $ConfigFile -Raw | ConvertFrom-Json
$root=Split-Path -Parent $PSScriptRoot
$target=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Valheim-Symbiosis-Repo'
$gh=$null
if($cfg.PSObject.Properties.Name -contains 'gh' -and $cfg.gh -and (Test-Path $cfg.gh)){ $gh=$cfg.gh }
if(-not $gh){ $cmd=Get-Command gh -EA 0; if($cmd){ $gh=$cmd.Source } }
if(-not $gh){ throw 'GitHub CLI fehlt. Setup-GitHub erneut starten.' }
if(!(Get-Command git -EA 0)){ throw 'Git wurde nicht gefunden. Git for Windows muss installiert sein.' }
if(!(Test-Path (Join-Path $target '.git'))){
  if(Test-Path $target){ Remove-Item $target -Recurse -Force }
  & $gh repo clone "$($cfg.owner)/$($cfg.repository)" $target
  if($LASTEXITCODE -ne 0){ throw 'Repository konnte nicht geklont werden.' }
}
Write-Host "`n==> Bereinige lokales Repository" -ForegroundColor Cyan
Get-ChildItem $target -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
$exclude=@('.git')
Get-ChildItem $root -Force | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object { Copy-Item $_.FullName $target -Recurse -Force }
Push-Location $target
try {
  # Git benoetigt fuer Commits eine lokale Identitaet. Wir setzen sie nur fuer dieses Repo.
  $gitName = (git config --local user.name 2>$null)
  $gitMail = (git config --local user.email 2>$null)
  if([string]::IsNullOrWhiteSpace($gitName) -or [string]::IsNullOrWhiteSpace($gitMail)) {
    Write-Host "`n==> Git-Identitaet fuer dieses Repository einrichten" -ForegroundColor Cyan
    $login=$null; $uid=$null
    try { $login = (& $gh api user --jq '.login' 2>$null | Select-Object -First 1).Trim() } catch {}
    try { $uid = (& $gh api user --jq '.id' 2>$null | Select-Object -First 1).Trim() } catch {}
    if([string]::IsNullOrWhiteSpace($login)){ $login = Read-Host 'GitHub Benutzername' }
    if([string]::IsNullOrWhiteSpace($uid)){
      $mail = Read-Host 'Git E-Mail (z.B. deine GitHub-Noreply-Adresse)'
    } else {
      $mail = "$uid+$login@users.noreply.github.com"
    }
    git config --local user.name $login
    git config --local user.email $mail
    if($LASTEXITCODE -ne 0){ throw 'Git-Identitaet konnte nicht gesetzt werden.' }
    Write-Host "Git-Identitaet: $login <$mail>" -ForegroundColor Green
  }

  git add -A
  $changes=git status --porcelain
  if(-not $changes){ Write-Host 'Keine Aenderungen zum Committen.' -ForegroundColor Yellow; exit 0 }
  git commit -m 'Clean repository structure for release workflow'
  if($LASTEXITCODE -ne 0){ throw 'Git Commit fehlgeschlagen.' }
  $answer=Read-Host 'Bereinigtes Repository jetzt zu GitHub pushen? (JA)'
  if($answer -eq 'JA'){
    git push -u origin HEAD
    if($LASTEXITCODE -ne 0){ throw 'Git Push fehlgeschlagen.' }
    Write-Host "`nFERTIG: Repository wurde bereinigt und gepusht." -ForegroundColor Green
  } else { Write-Host 'Commit erstellt, Push abgebrochen.' -ForegroundColor Yellow }
} finally { Pop-Location }
