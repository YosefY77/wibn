@echo off
rem Harmoni - the oldest saved version (Oct 10), for comparing. Separate data, separate port (5600).
cd /d "%~dp0harmoni-first-version"
set "PYW=%LOCALAPPDATA%\Programs\Python\Python312\pythonw.exe"
if not exist "%PYW%" set "PYW=pythonw"
set "MUSIC_PORT=5600"
start "" "%PYW%" server.py
