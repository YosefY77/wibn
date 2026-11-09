@echo off
setlocal EnableDelayedExpansion
set "OUT=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\outputs"
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "BK=%OUT%\Harmoni-backup-before-installer"
set "DATA=%LOCALAPPDATA%\HarmoniData"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"

echo [1/7] Closing Harmoni...
taskkill /IM Harmoni.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/7] Backing up the program to %BK% ...
robocopy "%OUT%\Harmoni-discord" "%BK%" /E /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 (echo BACKUP FAILED - nothing was changed. & pause & exit /b 1)

echo [3/7] Moving your data to %DATA% (only once, nothing is overwritten)...
if not exist "%DATA%" mkdir "%DATA%"
robocopy "%OUT%\Harmoni-discord\Harmoni" "%DATA%" profiles.json profiles.json.bak .harmoni_secret search_cache.json stream_cache.json artist_cache.json spotify_links.json spotify_config.json discord_config.json /XC /XN /XO /NFL /NDL /NJH /NJS /NP >nul
robocopy "%OUT%\Harmoni-discord\Harmoni\audio_cache" "%DATA%\audio_cache" /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP >nul

echo [4/7] Running tests (Python + interface)...
cd /d "%SRC%"
for %%T in (test_security.py test_speed.py test_bubble.py test_music_search.py test_search_filters.py test_performance.py test_repairs.py test_discord.py test_spotify_sync.py test_search_layout.py test_ui_smoke.py) do (
  if exist %%T (
    "%PY%" %%T >"%TEMP%\harmoni-test.log" 2>&1 || (type "%TEMP%\harmoni-test.log" & echo. & echo TEST %%T FAILED - program not rebuilt. & pause & exit /b 1)
    echo    ok %%T
  )
)
where node >nul 2>&1 && (
  for %%T in (test_frontend.cjs test_search_pages.cjs test_appearance.cjs test_discord.cjs test_volume_sync.cjs) do (
    if exist %%T (
      node %%T >"%TEMP%\harmoni-test.log" 2>&1 || (type "%TEMP%\harmoni-test.log" & echo. & echo TEST %%T FAILED - program not rebuilt. & pause & exit /b 1)
      echo    ok %%T
    )
  )
)

echo [5/7] Building Harmoni (about 1-2 minutes)...
"%PY%" -m PyInstaller HarmoniApp.spec --noconfirm --distpath "%OUT%\Harmoni-discord" --workpath "%SRC%\..\build-discord" > "%SRC%\..\build-installer.log" 2>&1
if errorlevel 1 (echo BUILD FAILED - see work\build-installer.log . Backup is in %BK% & pause & exit /b 1)

echo [6/7] Making the installer and installing...
set "ISCC="
for %%I in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe") do if exist %%I set "ISCC=%%~I"
for /f %%V in ('powershell -NoProfile -Command "Get-Date -Format yyyy.M.d.Hmm"') do set "VER=%%V"
if defined ISCC (
  "!ISCC!" /Q "/DSourceDir=%OUT%\Harmoni-discord\Harmoni" "/DMyAppVersion=!VER!" "/O%OUT%\installer" HarmoniSetup.iss
  if errorlevel 1 (echo Installer build failed - starting the portable copy instead. & set "ISCC=")
)
if defined ISCC (
  "%OUT%\installer\HarmoniSetup.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
  echo    Installed version !VER! to %LOCALAPPDATA%\Harmoni - Start menu: Harmoni
  echo    Installer saved at %OUT%\installer\HarmoniSetup.exe
  set "RUN=%LOCALAPPDATA%\Harmoni\Harmoni.exe"
) else (
  echo    Inno Setup 6 not found - skipped the installer. Get it free from https://jrsoftware.org/isdl.php
  set "RUN=%OUT%\Harmoni-discord\Harmoni\Harmoni.exe"
)

echo [7/7] Done! Starting Harmoni...
start "" "!RUN!"
timeout /t 3 >nul
