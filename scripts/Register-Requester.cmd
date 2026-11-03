@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-Requester.ps1" -WorkerDirectory "%~dp0."
set "DBE_REGISTER_EXIT=%errorlevel%"
pause
exit /b %DBE_REGISTER_EXIT%
