@echo off
rem Runs the Numidium server and restarts it if it stops. See run-server.ps1.
cd /d "%~dp0"
if exist "%~dp0run-server.ps1" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "E:\Jarldom\skymp\numidium-dev\run-server.ps1"
)
