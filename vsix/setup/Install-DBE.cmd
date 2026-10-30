@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DBE.ps1" %*
set "DBE_SETUP_EXIT=%ERRORLEVEL%"
echo.
if not "%DBE_SETUP_EXIT%"=="0" echo DBE installation did not complete. Read the message above.
pause
exit /b %DBE_SETUP_EXIT%
