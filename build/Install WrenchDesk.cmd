@echo off
REM Double-click this to install WrenchDesk on the shop PC.
REM -ExecutionPolicy Bypass applies to this one run only; nothing on the machine is changed.
title Install WrenchDesk
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
    echo.
    echo Installation did not finish. The message above says why.
    pause
)
