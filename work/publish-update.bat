@echo off
setlocal EnableDelayedExpansion
rem Publish a Harmoni update: everyone who has Harmoni installed gets an "update available" notice.
rem First run only: installs GitHub CLI if needed and asks you to log in to GitHub in your browser.
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "OUT=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\outputs\installer-release"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"
set "REPONAME=harmoni-app"
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESK=%%D"

echo [1/9] GitHub CLI...
set "GH="
for /f "delims=" %%G in ('where gh 2^>nul') do if not defined GH set "GH=%%G"
if not defined GH if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"
if not defined GH (
  echo    Installing GitHub CLI ^(one time^)...
  winget install --id GitHub.cli -e --silent --accept-source-agreements --accept-package-agreements
  if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"
)
if not defined GH (echo Could not install GitHub CLI. Get it from https://cli.github.com then run this again. & pause & exit /b 1)

"%GH%" auth status >nul 2>&1
if errorlevel 1 (
  echo.
  echo    You need to log in to GitHub once. A code will appear below - your browser opens,
  echo    paste the code there and approve. ^(No GitHub account? Create one free at github.com first.^)
  echo.
  "%GH%" auth login --hostname github.com --git-protocol https --web
  "%GH%" auth status >nul 2>&1 || (echo GitHub login did not finish. & pause & exit /b 1)
)
for /f "usebackq delims=" %%U in (`"%GH%" api user --jq .login`) do set "OWNER=%%U"
if not defined OWNER (echo Could not read your GitHub user name. & pause & exit /b 1)
set "REPO=%OWNER%/%REPONAME%"
echo    GitHub: %REPO%

echo [2/9] Making sure the releases page exists...
"%GH%" repo view "%REPO%" >nul 2>&1
if errorlevel 1 (
  "%GH%" repo create "%REPONAME%" --public --add-readme --description "Harmoni app releases" >nul
  if errorlevel 1 (echo Could not create %REPO% on GitHub. & pause & exit /b 1)
  echo    Created https://github.com/%REPO%
)

echo [3/9] What's new in this update? Notepad opens - write it, SAVE, then close Notepad.
set "NOTES=%SRC%\..\release-notes.txt"
> "%NOTES%" echo - 
start /wait notepad "%NOTES%"

for /f %%V in ('powershell -NoProfile -Command "Get-Date -Format yyyy.M.d.Hmm"') do set "VER=%%V"
echo [4/9] Version %VER%
cd /d "%SRC%"
"%PY%" set_version.py %VER% %REPO% || (pause & exit /b 1)

echo [5/9] Running tests...
set "HARMONI_REGION=none"
for %%T in (test_badges.py test_profiles_merge.py test_room_links.py test_upload.py test_downloads.py test_cuts.py test_alternate.py test_taste.py test_spotify_public.py test_search_quality.py test_spotify_2026.py test_search_retry.py test_updater.py test_room.py test_panel.py test_region.py test_security.py test_speed.py test_bubble.py test_music_search.py test_search_filters.py test_performance.py test_repairs.py test_discord.py test_spotify_sync.py test_search_layout.py test_ui_smoke.py) do (
  if exist %%T (
    "%PY%" %%T >"%TEMP%\harmoni-test.log" 2>&1 || (type "%TEMP%\harmoni-test.log" & echo. & echo TEST %%T FAILED - nothing was published. & pause & exit /b 1)
    echo    ok %%T
  )
)
where node >nul 2>&1 && (
  for %%T in (test_frontend.cjs test_search_pages.cjs test_appearance.cjs test_discord.cjs test_volume_sync.cjs) do (
    if exist %%T (
      node %%T >"%TEMP%\harmoni-test.log" 2>&1 || (type "%TEMP%\harmoni-test.log" & echo. & echo TEST %%T FAILED - nothing was published. & pause & exit /b 1)
      echo    ok %%T
    )
  )
)
set "HARMONI_REGION="

echo [6/9] Starting the Mac build on GitHub (it runs at the same time as the Windows build)...
"%GH%" release create "v%VER%" --draft --repo "%REPO%" --title "Harmoni %VER%" --notes-file "%NOTES%" >nul
if errorlevel 1 (echo Could not create the release on GitHub. & pause & exit /b 1)
set "MACOK=1"
"%PY%" "%SRC%\mac_publish.py" start "%GH%" %VER% %REPO% || set "MACOK=0"

echo [7/9] Building Windows (about 1-2 minutes)...
taskkill /IM Harmoni.exe /F >nul 2>&1
set "DIST=%TEMP%\harmoni-release-dist"
if exist "%DIST%" rd /s /q "%DIST%"
"%PY%" -m PyInstaller HarmoniApp.spec --noconfirm --distpath "%DIST%" --workpath "%TEMP%\harmoni-release-build" > "%SRC%\..\build-release.log" 2>&1
if errorlevel 1 ("%GH%" release delete "v%VER%" --repo "%REPO%" --yes >nul 2>&1 & echo BUILD FAILED - see work\build-release.log & pause & exit /b 1)
for %%F in (profiles.json profiles.json.bak .harmoni_secret discord_config.json spotify_links.json search_cache.json stream_cache.json artist_cache.json region.json panel_config.json) do if exist "%DIST%\Harmoni\%%F" del /q "%DIST%\Harmoni\%%F"
if exist "%DIST%\Harmoni\audio_cache" rd /s /q "%DIST%\Harmoni\audio_cache"

set "ISCC="
for %%I in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe") do if exist %%I set "ISCC=%%~I"
if not defined ISCC ("%GH%" release delete "v%VER%" --repo "%REPO%" --yes >nul 2>&1 & echo Inno Setup 6 is not installed: https://jrsoftware.org/isdl.php & pause & exit /b 1)
if not exist "%OUT%" mkdir "%OUT%"
"%ISCC%" /Q "/DSourceDir=%DIST%\Harmoni" "/DMyAppVersion=%VER%" "/O%OUT%" "/FHarmoni-Setup" HarmoniSetup.iss
if errorlevel 1 ("%GH%" release delete "v%VER%" --repo "%REPO%" --yes >nul 2>&1 & echo INSTALLER FAILED. & pause & exit /b 1)

echo [8/9] Uploading the Windows version...
"%GH%" release upload "v%VER%" "%OUT%\Harmoni-Setup.exe#Harmoni for Windows" --repo "%REPO%" --clobber
if errorlevel 1 ("%GH%" release delete "v%VER%" --repo "%REPO%" --yes >nul 2>&1 & echo PUBLISH FAILED - nothing changed for your friends. & pause & exit /b 1)
copy /y "%OUT%\Harmoni-Setup.exe" "%DESK%\Harmoni-Setup.exe" >nul
if "%MACOK%"=="1" (
  echo    Waiting for the Mac version so both appear together...
  "%PY%" "%SRC%\mac_publish.py" wait "%GH%" %VER% %REPO% || set "MACOK=0"
)
if "%MACOK%"=="0" (
  echo.
  echo    The Mac version did not build ^(see the message above^).
  choice /c YN /m "   Publish the Windows version alone now"
  if errorlevel 2 (echo Not published. The release is kept as a hidden draft on GitHub. & pause & exit /b 1)
)
"%GH%" release edit "v%VER%" --repo "%REPO%" --draft=false --latest >nul
if errorlevel 1 (echo PUBLISH FAILED - the release is still a hidden draft on GitHub. & pause & exit /b 1)
echo    Published v%VER%

echo [9/9] Updating Harmoni on this PC...
"%OUT%\Harmoni-Setup.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS
start "" "%LOCALAPPDATA%\Harmoni\Harmoni.exe"
echo.
echo Done! Version %VER% is live. Everyone with Harmoni installed will see the update notice
echo (within a few minutes of opening the app). New friends: send them %DESK%\Harmoni-Setup.exe
echo Releases page: https://github.com/%REPO%/releases
pause
