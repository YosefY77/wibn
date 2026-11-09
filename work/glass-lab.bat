@echo off
cd /d "%~dp0"
set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PY%" set "PY=py"
for %%M in (A B C D E F G) do (
  echo === METHOD %%M ===
  "%PY%" glass_lab.py %%M
)
echo done
timeout /t 5 >nul
