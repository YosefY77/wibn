@echo off
setlocal EnableDelayedExpansion
rem Makes a clean Harmoni installer you can send to friends.
rem It does NOT install anything on this PC and does NOT include your account, likes, playlists or history.
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "OUT=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\outputs\installer-friends"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESK=%%D"

echo [1/4] Looking for Inno Setup...
set "ISCC="
for %%I in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe") do if exist %%I set "ISCC=%%~I"
if not defined ISCC (echo Inno Setup 6 is not installed. Get it free from https://jrsoftware.org/isdl.php then run this again. & pause & exit /b 1)

echo [2/4] Running tests...
cd /d "%SRC%"
set "HARMONI_REGION=none"
for %%T in (test_room.py test_security.py test_repairs.py test_ui_smoke.py) do (
  if exist %%T (
    "%PY%" %%T >"%TEMP%\harmoni-test.log" 2>&1 || (type "%TEMP%\harmoni-test.log" & echo. & echo TEST %%T FAILED - installer not made. & pause & exit /b 1)
    echo    ok %%T
  )
)
set "HARMONI_REGION="

for /f %%V in ('powershell -NoProfile -Command "Get-Date -Format yyyy.M.d.Hmm"') do set "VER=%%V"
"%PY%" set_version.py %VER% >nul
echo [3/4] Building a clean copy of Harmoni (about 1-2 minutes)...
set "DIST=%TEMP%\harmoni-friends-dist"
if exist "%DIST%" rd /s /q "%DIST%"
"%PY%" -m PyInstaller HarmoniApp.spec --noconfirm --distpath "%DIST%" --workpath "%TEMP%\harmoni-friends-build" > "%SRC%\..\build-friends.log" 2>&1
if errorlevel 1 (echo BUILD FAILED - see work\build-friends.log & pause & exit /b 1)
rem safety: make sure no personal files ended up in the build
for %%F in (profiles.json profiles.json.bak .harmoni_secret discord_config.json spotify_links.json search_cache.json stream_cache.json artist_cache.json region.json panel_config.json) do if exist "%DIST%\Harmoni\%%F" del /q "%DIST%\Harmoni\%%F"
if exist "%DIST%\Harmoni\audio_cache" rd /s /q "%DIST%\Harmoni\audio_cache"

echo [4/4] Making the installer...
if not exist "%OUT%" mkdir "%OUT%"
"%ISCC%" /Q "/DSourceDir=%DIST%\Harmoni" "/DMyAppVersion=!VER!" "/O%OUT%" "/FHarmoni-Setup" HarmoniSetup.iss
if errorlevel 1 (echo INSTALLER FAILED. & pause & exit /b 1)
copy /y "%OUT%\Harmoni-Setup.exe" "%DESK%\Harmoni-Setup.exe" >nul
echo.
echo Done! Send this file to your friends:
echo    %DESK%\Harmoni-Setup.exe
echo (a copy is also in %OUT%)
explorer /select,"%DESK%\Harmoni-Setup.exe"
pause
