@echo off
chcp 65001 >nul
title Harmoni Hub - Cloudflare deploy
cd /d "%~dp0harmoni\hub"
echo ==================================================
echo   Harmoni Hub: upload the shared accounts server
echo ==================================================
where node >nul 2>&1
if errorlevel 1 (
  echo Node.js not found - installing it with winget...
  winget install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements
  set "PATH=%PATH%;%ProgramFiles%\nodejs"
)
where node >nul 2>&1
if errorlevel 1 (
  echo.
  echo [x] Node.js is still missing. Close this window, open it again, and retry.
  pause
  exit /b 1
)
echo.
echo [1/3] Sign in to Cloudflare (a browser page will open - press Allow)...
rem لو مسجل دخول من قبل ما نفتح صفحة الدخول مرة ثانية (كانت تطلع خطأ CSRF لو انفتحت مرتين)
call npx -y wrangler@4 whoami 2>nul | findstr /i "associated with the email" >nul
if errorlevel 1 (
  call npx -y wrangler@4 login
  call npx -y wrangler@4 whoami 2>nul | findstr /i "associated with the email" >nul
  if errorlevel 1 goto fail
) else (
  echo    Already signed in.
)
echo.
echo [2/3] Storage for big profile pictures (Cloudflare R2)...
call npx -y wrangler@4 r2 bucket create harmoni-media >"%TEMP%\harmoni-r2.log" 2>&1
set "CFG=wrangler.toml"
call npx -y wrangler@4 r2 bucket list 2>nul | findstr /i "harmoni-media" >nul
if errorlevel 1 set "CFG=wrangler.no-media.toml"
if "%CFG%"=="wrangler.no-media.toml" (
  type "%TEMP%\harmoni-r2.log"
  echo.
  echo    R2 is not enabled on your Cloudflare account yet - big GIF pictures stay off for now.
  echo    To enable: dash.cloudflare.com - R2 Object Storage - Enable ^(free up to 10 GB^), then run this again.
) else (
  echo    OK
)
echo.
echo [2/3] Uploading the server + linking the database and hub.harmoni.click ...
call npx -y wrangler@4 deploy --config %CFG%
if errorlevel 1 goto fail
echo.
echo [3/3] Checking...
timeout /t 8 >nul
curl -s https://hub.harmoni.click/health
echo.
echo.
echo Done. If you see "ok":true above, the server is live.
echo (If it says it can't connect, wait a minute - the domain takes a moment the first time.)
pause
exit /b 0
:fail
echo.
echo [x] Something failed - send a screenshot of this window.
pause
exit /b 1
