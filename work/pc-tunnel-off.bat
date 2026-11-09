@echo off
chcp 65001 >nul
title Harmoni - stop PC tunnel
set "DATA=%LOCALAPPDATA%\HarmoniData"
if not exist "%DATA%" mkdir "%DATA%"
echo the site runs on the Raspberry Pi> "%DATA%\tunnel_disabled"
taskkill /f /im cloudflared.exe >nul 2>&1
sc query cloudflared >nul 2>&1 && (
  echo [!] cloudflared is also installed as a Windows service.
  echo     Run this file as Administrator once to stop it.
  sc stop cloudflared >nul 2>&1
  sc config cloudflared start= disabled >nul 2>&1
)
echo.
echo OK - this PC no longer serves harmoni.click. The Raspberry Pi does.
echo To undo: pc-tunnel-on.bat
pause
