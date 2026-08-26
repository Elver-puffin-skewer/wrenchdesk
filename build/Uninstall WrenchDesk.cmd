@echo off
REM Removes the shortcuts and program files. Your shop data in Documents\WrenchDesk is kept.
title Uninstall WrenchDesk
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
