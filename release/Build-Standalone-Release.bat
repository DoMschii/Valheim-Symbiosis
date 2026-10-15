@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Standalone-Release.ps1"
pause
