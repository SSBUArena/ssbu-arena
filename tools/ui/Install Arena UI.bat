@echo off
cd /d "%~dp0..\.."
set "PY=python"
if exist deps\python\python.exe set "PY=%CD%\deps\python\python.exe"
"%PY%" tools\ui\install_menu.py
if errorlevel 1 (
  echo Arena UI installation failed. See the message above.
  pause
)
