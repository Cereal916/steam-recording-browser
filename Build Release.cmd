@echo off
setlocal
cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7.6+ is required on the development machine for release packaging.
  echo Rider development builds do not require this script.
  pause
  exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0eng\Build-Release.ps1"
