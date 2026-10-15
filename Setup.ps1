$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Run-Module([string]$Name, [string]$Script) {
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script
    if ($LASTEXITCODE -ne 0) {
        throw "$Name setup failed with exit code $LASTEXITCODE"
    }
}

Run-Module 'Valheim Workers Core v0.6.0 (RC1 code line)' (Join-Path $root 'src\WorkersCore\Setup.ps1')
Run-Module 'Valheim Workers Mount v4.5.2' (Join-Path $root 'src\Mount\Setup.ps1')
Run-Module 'Valheim Workers DrakeForm v0.7.0' (Join-Path $root 'src\DrakeForm\Setup.ps1')
Run-Module 'Valheim Ego v1.6.50' (Join-Path $root 'src\Ego\Setup.ps1')

Write-Host "`nDone: Symbiosis gameplay modules installed." -ForegroundColor Green
Write-Host 'The preloader updater and GitHub release tooling remain separate projects.' -ForegroundColor DarkGray
