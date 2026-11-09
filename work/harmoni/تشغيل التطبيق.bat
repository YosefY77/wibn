@echo off
chcp 65001 >nul
title Harmoni
cd /d "%~dp0"
set MUSIC_PORT=5000
python server.py
pause
