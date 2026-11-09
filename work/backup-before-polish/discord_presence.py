"""Optional local Discord Rich Presence. Never runs on the playback thread."""
import ctypes
import json
import math
import os
import re
import struct
import threading
import time
import uuid


# خيارات الظهور (كلها مفعلة افتراضياً = نفس السلوك القديم)
DEFAULT_OPTIONS = {
    'show_title': True,     # اسم الأغنية
    'show_artist': True,    # اسم الفنان
    'show_cover': True,     # صورة الأغنية
    'show_time': True,      # الوقت (المتبقي/المنقضي)
    'show_button': False,   # زر "استمع" برابط الأغنية
    'show_in_rooms': True,  # يظهر وأنت في غرفة استماع
}


def clean_options(value):
    value = value if isinstance(value, dict) else {}
    return {k: bool(value.get(k, d)) for k, d in DEFAULT_OPTIONS.items()}


def activity_for(state, now=None, options=None):
    now = time.time() if now is None else now
    opts = clean_options(options)
    track = state.get('track') or {}
    if not state.get('playing') or not track.get('title'):
        return None
    if state.get('in_room') and not opts['show_in_rooms']:
        return None
    def number(value):
        try:
            value = float(value)
            return max(0, value) if math.isfinite(value) else 0
        except (ValueError, TypeError):
            return 0
    position = number(state.get('position'))
    duration = number(track.get('duration'))
    title = str(track['title'])[:128] if opts['show_title'] else 'Listening on Harmoni'
    activity = {'type': 2, 'details': title}
    if opts['show_artist'] and track.get('artist'):
        activity['state'] = str(track.get('artist'))[:128]
        # قائمة الأعضاء في ديسكورد تعرض اسم المغني بدل اسم التطبيق ("Listening to <artist>")
        activity['status_display_type'] = 1   # STATE
    elif opts['show_title']:
        activity['state'] = 'Harmoni'
        activity['status_display_type'] = 2   # DETAILS: اسم الأغنية لو اسم المغني مخفي
    if opts['show_time']:
        activity['timestamps'] = {'start': int(now - position)}
        if duration > position:
            activity['timestamps']['end'] = int(now + duration - position)
    thumbnail = str(track.get('thumbnail') or '')
    if opts['show_cover'] and thumbnail.startswith('https://') and len(thumbnail) < 1024:
        activity['assets'] = {'large_image': thumbnail, 'large_text': title}
    url = str(track.get('web_url') or '')
    if opts['show_button'] and opts['show_title'] and re.match(r'^https://[^\s]{8,500}$', url):
        activity['buttons'] = [{'label': 'Listen', 'url': url}]
    return activity


class DiscordPipe:
    def __init__(self, client_id):
        if os.name != 'nt':
            raise OSError('Windows Discord desktop required')
        import msvcrt
        self.file = None
        self.peek = ctypes.WinDLL('kernel32', use_last_error=True).PeekNamedPipe
        self.peek.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32,
                              ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
        self.peek.restype = ctypes.c_int
        for index in range(10):
            try:
                self.file = open(r'\\.\pipe\discord-ipc-' + str(index), 'r+b', buffering=0)
                break
            except OSError:
                continue
        if self.file is None:
            raise OSError('Discord is not running')
        self.handle = msvcrt.get_osfhandle(self.file.fileno())
        try:
            self.send(0, {'v': 1, 'client_id': client_id})
            answer = self.receive()
            if answer.get('evt') != 'READY':
                raise OSError('Discord handshake rejected')
        except Exception:
            self.close()
            raise

    def send(self, opcode, payload):
        body = json.dumps(payload, ensure_ascii=False).encode('utf-8')
        frame = struct.pack('<II', opcode, len(body)) + body
        if self.file.write(frame) != len(frame):
            raise OSError('Incomplete Discord write')

    def read(self, size, deadline):
        result = bytearray()
        while len(result) < size:
            if time.monotonic() > deadline:
                raise TimeoutError('Discord response timed out')
            available = ctypes.c_uint32()
            if not self.peek(self.handle, None, 0, None, ctypes.byref(available), None):
                raise OSError('Discord disconnected')
            if not available.value:
                time.sleep(.025)
                continue
            data = self.file.read(min(size - len(result), available.value))
            if not data:
                raise OSError('Discord disconnected')
            result.extend(data)
        return bytes(result)

    def receive(self):
        deadline = time.monotonic() + 5
        while True:
            opcode, size = struct.unpack('<II', self.read(8, deadline))
            if size > 1024 * 1024:
                raise OSError('Invalid Discord frame')
            data = json.loads(self.read(size, deadline))
            if opcode == 3:
                self.send(4, data)
            elif opcode == 1:
                return data
            elif opcode == 2:
                raise OSError('Discord closed connection')

    def update(self, activity):
        nonce = str(uuid.uuid4())
        self.send(1, {'cmd': 'SET_ACTIVITY', 'args': {'pid': os.getpid(), 'activity': activity}, 'nonce': nonce})
        for _ in range(10):
            answer = self.receive()
            if answer.get('nonce') == nonce:
                if answer.get('evt') == 'ERROR':
                    raise OSError('Discord rejected activity')
                return
        raise OSError('Discord did not acknowledge activity')

    def close(self):
        if self.file:
            self.file.close()
            self.file = None


class Presence:
    def __init__(self, directory):
        self.path = os.path.join(directory, 'discord_config.json')
        self.lock = threading.Lock()
        self.config = {'enabled': False, 'client_id': '', 'options': clean_options(None)}
        try:
            with open(self.path, encoding='utf-8') as f:
                value = json.load(f)
            self.config['options'] = clean_options(value.get('options'))
            if re.fullmatch(r'[0-9]{17,20}', str(value.get('client_id', ''))):
                self.config.update({'enabled': bool(value.get('enabled')), 'client_id': str(value['client_id'])})
        except (OSError, ValueError, TypeError, AttributeError):
            pass
        self.state = {}
        self.last_seen = 0
        self.status = 'disabled'
        self.stop = threading.Event()
        self.thread = None

    def start(self):
        with self.lock:
            if self.thread is None:
                self.thread = threading.Thread(target=self.run, daemon=True)
                self.thread.start()

    def snapshot(self):
        with self.lock:
            return dict(self.config, status=self.status)

    def configure(self, value):
        if not isinstance(value, dict):
            raise ValueError('invalid_config')
        client_id = str(value.get('client_id') or '').strip()
        enabled = value.get('enabled') is True
        if client_id and not re.fullmatch(r'[0-9]{17,20}', client_id):
            raise ValueError('invalid_id')
        if enabled and not client_id:
            raise ValueError('missing_id')
        old = self.config.get('options') or {}
        config = {'enabled': enabled, 'client_id': client_id,
                  'options': clean_options(value.get('options') if isinstance(value.get('options'), dict) else old)}
        with self.lock:
            tmp = self.path + '.tmp'
            with open(tmp, 'w', encoding='utf-8') as f:
                json.dump(config, f)
            os.replace(tmp, self.path)
            self.config = config
        self.start()

    def report(self, state):
        with self.lock:
            self.state = state
            self.last_seen = time.monotonic()

    def run(self):
        pipe, connected_id, previous = None, None, object()
        try:
            while not self.stop.is_set():
                with self.lock:
                    config = dict(self.config)
                    age = time.monotonic() - self.last_seen
                    state = dict(self.state) if age < 20 else {}
                if state.get('playing'):
                    try:
                        state['position'] = float(state.get('position') or 0) + age
                    except (TypeError, ValueError):
                        state['position'] = 0
                try:
                    if pipe and (not config['enabled'] or connected_id != config['client_id']):
                        try:
                            pipe.update(None)
                        finally:
                            pipe.close()
                            pipe = None
                    if not config['enabled']:
                        self.status = 'disabled'
                    else:
                        if pipe is None:
                            pipe = DiscordPipe(config['client_id'])
                            connected_id, previous = config['client_id'], object()
                        activity = activity_for(state, options=config.get('options'))
                        # Ignore sub-second clock drift, but propagate seeks and track changes.
                        signature = json.dumps(activity, sort_keys=True)
                        if signature != previous:
                            pipe.update(activity)
                            previous = signature
                        self.status = 'connected'
                except Exception:
                    if pipe:
                        pipe.close()
                    pipe, previous = None, object()
                    self.status = 'unavailable'
                self.stop.wait(5)
        finally:
            if pipe:
                pipe.close()

    def close(self):
        self.stop.set()


def install(app, directory):
    from flask import request, jsonify, abort
    presence = Presence(directory)

    def local_only():
        if (request.remote_addr not in ('127.0.0.1', '::1')
                or request.host.split(':')[0] not in ('127.0.0.1', 'localhost')
                or request.headers.get('X-Harmoni-Desktop') != '1'):
            abort(403)

    @app.route('/api/discord/config', methods=['GET', 'POST'])
    def discord_config():
        local_only()
        if request.method == 'POST':
            try:
                presence.configure(request.get_json(silent=True) or {})
            except ValueError as e:
                return jsonify(error=str(e)), 400
        return jsonify(presence.snapshot())

    @app.route('/api/discord/state', methods=['POST'])
    def discord_state():
        local_only()
        data = request.get_json(silent=True) or {}
        if not isinstance(data, dict) or not isinstance(data.get('track', {}), (dict, type(None))):
            abort(400)
        presence.report(data)
        return jsonify(ok=True)

    return presence
