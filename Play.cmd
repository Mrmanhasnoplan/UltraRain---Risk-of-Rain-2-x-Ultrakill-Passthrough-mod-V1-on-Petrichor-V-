@echo off
rem Double-click to start Risk of Rain 2 with the mod. The window stays open if something is wrong.
if not exist "%~dp0tools\Play.ps1" (
    echo The rest of the mod is not next to this file. Extract the whole ZIP to a folder first, then run Play.cmd from there.
    echo.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Play.ps1" %*
if errorlevel 1 (
    echo.
    pause
)
