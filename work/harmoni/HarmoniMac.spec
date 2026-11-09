# -*- mode: python ; coding: utf-8 -*-
# نسخة الماك: تنبني على GitHub Actions (macos-14 / Apple Silicon) — شوف .github/workflows/mac-build.yml
from PyInstaller.utils.hooks import collect_all
import json
import os

datas = [('static', 'static'), ('version.json', '.')]
if os.path.exists('spotify_config.json'):
    datas.append(('spotify_config.json', '.'))
binaries = []
hiddenimports = [
    'engineio.async_drivers',
    'engineio.async_drivers.threading',
    'socketio',
    'simple_websocket',
    'simple_websocket.ws',
    'webview.platforms.cocoa',
]
for pkg in ('yt_dlp', 'yt_dlp_ejs'):
    r = collect_all(pkg)
    datas += r[0]; binaries += r[1]; hiddenimports += r[2]

try:
    VERSION = json.load(open('version.json', encoding='utf-8')).get('version') or '1.0'
except Exception:
    VERSION = '1.0'

a = Analysis(
    ['server.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=['keyboard', 'pycaw', 'comtypes', 'clr', 'pythonnet', 'tkinter'],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='Harmoni',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch='arm64',
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, name='Harmoni')
app = BUNDLE(
    coll,
    name='Harmoni.app',
    icon='mac/harmoni.icns',
    bundle_identifier='click.harmoni.app',
    version=VERSION,
    info_plist={
        'CFBundleName': 'Harmoni',
        'CFBundleDisplayName': 'Harmoni',
        'CFBundleShortVersionString': VERSION,
        'NSHighResolutionCapable': True,
        'NSAppSleepDisabled': True,   # بدون App Nap: السيرفر يبقى سريع والأغاني ما تعلق لما النافذة ورا
        'NSSupportsAutomaticTermination': False,
        'NSSupportsSuddenTermination': False,
        'LSMinimumSystemVersion': '12.0',
        'LSApplicationCategoryType': 'public.app-category.music',
        'NSAppTransportSecurity': {'NSAllowsArbitraryLoads': True, 'NSAllowsLocalNetworking': True},
    },
)
