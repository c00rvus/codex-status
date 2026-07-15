@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Standalone.ps1" %*
exit /b %ERRORLEVEL%
