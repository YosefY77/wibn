# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.utils.hooks import collect_all
import os
import sys

sys.path.insert(0, os.path.join(SPECPATH, 'vendor'))

datas = [('static', 'static'), ('spotify_config.json', '.'), ('version.json', '.')]
binaries = []
hiddenimports = [
    'engineio.async_drivers',
    'engineio.async_drivers.threading',
    'socketio',
    'simple_websocket',
    'simple_websocket.ws',
]
tmp_ret = collect_all('yt_dlp')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]
ejs_ret = collect_all('yt_dlp_ejs')
datas += ejs_ret[0]; binaries += ejs_ret[1]; hiddenimports += ejs_ret[2]


a = Analysis(
    ['server.py'],
    pathex=[os.path.join(SPECPATH, 'vendor')],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
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
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['static/harmoni.ico'],
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='Harmoni',
)
