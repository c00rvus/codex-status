@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop-Standalone.ps1" %*
exit /b %ERRORLEVEL%
