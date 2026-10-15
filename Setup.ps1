$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host 'Installing Valheim Workers Mount v4.5.1...' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\Mount\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Mount setup failed with exit code $LASTEXITCODE" }

Write-Host 'Installing Valheim Workers DrakeForm v0.7.0...' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\DrakeForm\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "DrakeForm setup failed with exit code $LASTEXITCODE" }

Write-Host 'Installing Valheim Ego v1.6.49...' -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'src\Ego\Setup.ps1')
if ($LASTEXITCODE -ne 0) { throw "Ego setup failed with exit code $LASTEXITCODE" }

Write-Host 'Done: Symbiosis gameplay modules installed. Updater and release tooling are separate projects.' -ForegroundColor Green
