@echo off
setlocal
rem ينقل النسخ القديمة لمجلد old-versions (ما يحذف شي). يخلي Harmoni-discord وآخر نسختين احتياطيتين.
set "OUT=C:\Users\YSIF\Documents\Codex\2026-10-09\new-chat\outputs"
set "OLD=%OUT%\old-versions"
if not exist "%OLD%" mkdir "%OLD%"
for %%D in (Harmoni-backup-2026-10-09 Harmoni-backup-2026-10-10-before-redesign Harmoni-backup-before-library Harmoni-backup-before-link-fix Harmoni-backup-before-music-search Harmoni-backup-before-performance Harmoni-backup-before-search-layout Harmoni-backup-before-speed Harmoni-backup-before-sync Harmoni-design-before-classic Harmoni-fast Harmoni-fixed Harmoni-fixed-v2 Harmoni-link-fix Harmoni-music-search Harmoni-redesigned Harmoni-search-update Harmoni-updated Harmoni-backup-before-filters Harmoni-backup-before-bubble Harmoni-backup-bubble-v1 Harmoni-backup-bubble-v2 Harmoni-backup-bubble-v3 Harmoni-backup-bubble-v4 Harmoni-backup-bubble-v5 Harmoni-backup-bubble-v6 Harmoni-backup-before-playlist-page) do (
  if exist "%OUT%\%%D" (
    echo Moving %%D ...
    move "%OUT%\%%D" "%OLD%\" >nul || echo   could not move %%D - is it open?
  )
)
echo.
echo Done. Kept: Harmoni-discord (the app), Harmoni-backup-before-stats, Harmoni-backup-before-security
echo Old copies are in: %OLD%   (delete that folder yourself when you're sure)
pause
