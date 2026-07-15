@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install-Standalone.ps1" %*
exit /b %errorlevel%
