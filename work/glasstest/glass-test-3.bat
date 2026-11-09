@echo off
cd /d "%~dp0"
"%LOCALAPPDATA%\Programs\Python\Python312\python.exe" glass_test_v3.py
if errorlevel 1 pause
