@echo off
setlocal EnableExtensions
chcp 65001 >nul
title Harmoni - Raspberry Pi package
cd /d "%~dp0"

set "SRC=%~dp0harmoni"
set "DATA=%LOCALAPPDATA%\HarmoniData"
set "CF=%USERPROFILE%\.cloudflared"
set "OUT=%~dp0pi-package"
set "PKG=%OUT%\harmoni-pi"

if not exist "%SRC%\server.py" ( echo [x] harmoni\server.py not found next to this file & pause & exit /b 1 )
if not exist "%SRC%\pi\setup-pi.sh" ( echo [x] harmoni\pi\setup-pi.sh not found & pause & exit /b 1 )
if not exist "%CF%\config.yml" ( echo [x] %CF%\config.yml not found - the tunnel is not set up on this PC & pause & exit /b 1 )

echo.
echo === 1/3  Building the package ===
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%PKG%\harmoni" "%PKG%\data" "%PKG%\cloudflared"

robocopy "%SRC%" "%PKG%\harmoni" /E /NFL /NDL /NJH /NJS /NP ^
  /XD __pycache__ audio_cache build dist vendor .venv node_modules pi ^
  /XF test_*.* *.log *.spec *.iss *.pyc stream_cache.json search_cache.json artist_cache.json >nul
copy /y "%SRC%\pi\setup-pi.sh" "%PKG%\setup-pi.sh" >nul

for %%F in (profiles.json profiles.json.bak .harmoni_secret spotify_links.json spotify_config.json region.json artist_cache.json) do (
  if exist "%DATA%\%%F" copy /y "%DATA%\%%F" "%PKG%\data\" >nul
)
if exist "%DATA%\uploads" robocopy "%DATA%\uploads" "%PKG%\data\uploads" /E /NFL /NDL /NJH /NJS /NP >nul
if exist "%DATA%\covers"  robocopy "%DATA%\covers"  "%PKG%\data\covers"  /E /NFL /NDL /NJH /NJS /NP >nul

copy /y "%CF%\config.yml" "%PKG%\cloudflared\" >nul
copy /y "%CF%\*.json" "%PKG%\cloudflared\" >nul

pushd "%OUT%"
tar -czf harmoni-pi.tar.gz harmoni-pi || ( echo [x] tar failed & popd & pause & exit /b 1 )
popd
echo     OK: %OUT%\harmoni-pi.tar.gz

echo.
echo === 2/3  Sending to the Raspberry Pi ===
echo Type the Pi login, e.g.  pi@raspberrypi.local   or   pi@192.168.1.50
set /p "PIHOST=Pi address: "
if "%PIHOST%"=="" ( echo [x] no address & pause & exit /b 1 )
echo (it will ask for the Pi password - type it, nothing shows while typing)
scp "%OUT%\harmoni-pi.tar.gz" "%PIHOST%:~/" || ( echo [x] copy failed - check the address and that SSH is enabled on the Pi & pause & exit /b 1 )

echo.
echo === 3/3  Installing on the Pi (Pi 3 / Zero: 10-30 minutes) ===
ssh -t "%PIHOST%" "rm -rf ~/harmoni-pi && tar xzf ~/harmoni-pi.tar.gz -C ~ && sudo bash ~/harmoni-pi/setup-pi.sh"
if errorlevel 1 ( echo [x] setup reported an error - send me the last lines above & pause & exit /b 1 )

echo.
echo Done. If the Pi printed the green "done" box, now run  pc-tunnel-off.bat
pause
