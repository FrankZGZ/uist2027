@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Preview.ps1" -NoRelay
if errorlevel 1 pause
