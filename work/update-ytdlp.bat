@echo off
setlocal
rem تحديث آمن لدعم يوتيوب (yt-dlp). لو التحديث ما اشتغل يرجّع القديم تلقائياً.
set "WORK=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\work"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=python"
cd /d "%WORK%\harmoni"
"%PY%" update_ytdlp.py
set "CODE=%ERRORLEVEL%"
if "%CODE%"=="3" (echo. & echo yt-dlp is already the newest and playback works - nothing to rebuild. & pause & exit /b 0)
if "%CODE%"=="2" (echo. & echo No internet - try again later. & pause & exit /b 1)
if not "%CODE%"=="0" (echo. & echo Update was not applied - your current version is unchanged. & pause & exit /b 1)
echo.
echo yt-dlp updated and tested OK - rebuilding Harmoni...
call "%WORK%\update-harmoni-v12.bat"
