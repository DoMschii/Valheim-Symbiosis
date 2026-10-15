$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host 'Installing Valheim Workers Mount v4.5.1...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\Mount\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Mount setup failed with exit code $LASTEXITCODE" }
Write-Host 'Installing Valheim Workers DrakeForm v0.7.0...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\DrakeForm\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "DrakeForm setup failed with exit code $LASTEXITCODE" }
Write-Host 'Installing Valheim Ego v1.6.49...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\Ego\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Ego setup failed with exit code $LASTEXITCODE" }
Write-Host 'Installing Valheim Symbiosis Updater v4.5.1...'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\Updater\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Updater setup failed with exit code $LASTEXITCODE" }
Write-Host 'Done: Mount v4.5.1 + DrakeForm v0.7.0 + Ego v1.6.49 + Updater v4.5.1 installed.' -ForegroundColor Green
