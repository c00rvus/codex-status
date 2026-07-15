@echo off
setlocal
call "%~dp0Run-Standalone.cmd" -SkipBuild -OpenSettings
exit /b %ERRORLEVEL%
