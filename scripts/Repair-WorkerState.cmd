@echo off
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Repair-WorkerState.ps1" -WorkerDirectory "%~dp0."
if errorlevel 1 (
  pause
  exit /b 1
)
if exist "%~dp0start-worker.cmd" (
  call "%~dp0start-worker.cmd"
) else (
  echo State repair finished. Run the existing Worker start command.
  pause
)
