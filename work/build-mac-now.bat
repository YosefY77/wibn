@echo off
setlocal
rem Builds the Mac version for the LATEST published release (no new Windows build).
set "SRC=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work\harmoni"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"
set "GH="
for /f "delims=" %%G in ('where gh 2^>nul') do if not defined GH set "GH=%%G"
if not defined GH if exist "%ProgramFiles%\GitHub CLI\gh.exe" set "GH=%ProgramFiles%\GitHub CLI\gh.exe"
if not defined GH (echo GitHub CLI not found - run publish-update.bat once first. & pause & exit /b 1)
for /f "usebackq delims=" %%U in (`"%GH%" api user --jq .login`) do set "OWNER=%%U"
set "REPO=%OWNER%/harmoni-app"
for /f "usebackq delims=" %%T in (`"%GH%" api repos/%REPO%/releases/latest --jq .tag_name`) do set "TAG=%%T"
if not defined TAG (echo No release found on %REPO%. & pause & exit /b 1)
set "VER=%TAG:~1%"
echo Building the Mac version for %TAG% ...
"%PY%" "%SRC%\mac_publish.py" start "%GH%" %VER% %REPO% || (pause & exit /b 1)
"%PY%" "%SRC%\mac_publish.py" wait "%GH%" %VER% %REPO%
if errorlevel 1 (echo. & echo The Mac version was NOT added. Tell Claude - details are in work\mac-build.log & pause & exit /b 1)
"%PY%" "%SRC%\mac_publish.py" label "%GH%" %VER% %REPO%
echo.
echo Done - "Harmoni for Mac" is now next to "Harmoni for Windows":
echo https://github.com/%REPO%/releases/tag/%TAG%
pause
