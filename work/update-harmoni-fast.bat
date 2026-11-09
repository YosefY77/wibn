@echo off
rem Quick Harmoni update on this PC: interface-only changes copy in seconds; code changes rebuild with the cache.
rem For a full clean build + installer use update-harmoni-v12.bat. To publish to friends use publish-update.bat.
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"
cd /d "%SRC%"
"%PY%" "%SRC%\fast_update.py" %*
if errorlevel 1 (echo. & pause & exit /b 1)
timeout /t 3 >nul
