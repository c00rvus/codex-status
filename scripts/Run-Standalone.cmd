@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Standalone.ps1" %*
exit /b %ERRORLEVEL%
