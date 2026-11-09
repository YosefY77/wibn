@echo off
chcp 65001 >nul
title Harmoni - PC tunnel back on
del /q "%LOCALAPPDATA%\HarmoniData\tunnel_disabled" >nul 2>&1
echo OK - restart Harmoni and this PC will serve harmoni.click again.
echo (stop it on the Pi first:  sudo systemctl disable --now harmoni-tunnel)
pause
