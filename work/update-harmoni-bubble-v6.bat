@echo off
setlocal
set "OUT=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\outputs"
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "BK=%OUT%\Harmoni-backup-bubble-v5"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"

echo [1/5] Closing Harmoni...
taskkill /IM Harmoni.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/5] Backing up the whole program to %BK% ...
robocopy "%OUT%\Harmoni-discord" "%BK%" /E /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 (echo BACKUP FAILED - nothing was changed. & pause & exit /b 1)

echo [3/5] Running tests...
cd /d "%SRC%"
"%PY%" test_bubble.py || (echo TESTS FAILED - program not rebuilt. & pause & exit /b 1)

echo [4/5] Building Harmoni (about 1-2 minutes)...
"%PY%" -m PyInstaller HarmoniApp.spec --noconfirm --distpath "%OUT%\Harmoni-discord" --workpath "%SRC%\..\build-discord" > "%SRC%\..\build-bubble6.log" 2>&1
if errorlevel 1 (echo BUILD FAILED - see work\build-bubble6.log . Backup is in %BK% & pause & exit /b 1)

echo [5/5] Restoring your data (profiles, cache, links, discord)...
robocopy "%BK%\Harmoni" "%OUT%\Harmoni-discord\Harmoni" /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP >nul

echo Done! Starting Harmoni...
start "" "%OUT%\Harmoni-discord\Harmoni\Harmoni.exe"
timeout /t 3 >nul
