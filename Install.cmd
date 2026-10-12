@echo off
rem Double-click to set the mod up. The window stays open so any message can be read.
if not exist "%~dp0tools\Install.ps1" (
    echo The rest of the mod is not next to this file. Extract the whole ZIP to a folder first, then run Install.cmd from there.
    echo.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Install.ps1" %*
echo.
pause
