@echo off
cd /d "%~dp0"
set MUSIC_PORT=5000
start "Harmoni Server" /min python server.py
