@echo off
chcp 65001 >nul
title Harmoni - restore following
cd /d "%~dp0"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=py"
echo Closing Harmoni...
taskkill /f /im Harmoni.exe >nul 2>&1
timeout /t 2 /nobreak >nul
"%PY%" "%~dp0restore-following.py"
if errorlevel 1 ( echo [x] failed - nothing was changed & pause & exit /b 1 )
echo Starting Harmoni...
start "" "%LOCALAPPDATA%\Harmoni\Harmoni.exe"
timeout /t 3 /nobreak >nul
