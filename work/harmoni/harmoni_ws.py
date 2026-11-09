"""عميل WebSocket صغير (مكتبة بايثون العادية بس) — يوصل هارموني بغرف الاستماع على السيرفر المشترك."""
import base64
import hashlib
import os
import socket
import ssl
import struct
import threading
import urllib.parse

_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class Closed(Exception):
    pass


class WebSocket:
    def __init__(self, url, timeout=10, headers=None):
        u = urllib.parse.urlsplit(url)
        secure = u.scheme in ("wss", "https")
        host = u.hostname
        port = u.port or (443 if secure else 80)
        path = (u.path or "/") + ("?" + u.query if u.query else "")
        raw = socket.create_connection((host, port), timeout=timeout)
        if secure:
            ctx = ssl.create_default_context()
            raw = ctx.wrap_socket(raw, server_hostname=host)
        self.sock = raw
        self._send_lock = threading.Lock()
        self._buf = b""
        self._parts = []
        self.closed = False
        key = base64.b64encode(os.urandom(16)).decode()
        lines = ["GET %s HTTP/1.1" % path, "Host: %s" % (host if port in (80, 443) else "%s:%d" % (host, port)),
                 "Upgrade: websocket", "Connection: Upgrade", "Sec-WebSocket-Key: " + key,
                 "Sec-WebSocket-Version: 13", "User-Agent: Harmoni"]
        for k, v in (headers or {}).items():
            lines.append("%s: %s" % (k, v))
        self.sock.sendall(("\r\n".join(lines) + "\r\n\r\n").encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise Closed("handshake: connection closed")
            head += chunk
            if len(head) > 65536:
                raise Closed("handshake: header too big")
        head, self._buf = head.split(b"\r\n\r\n", 1)
        status = head.split(b"\r\n", 1)[0]
        if b" 101 " not in status + b" ":
            raise Closed("handshake: " + status.decode("latin-1"))
        want = base64.b64encode(hashlib.sha1((key + _GUID).encode()).digest())
        if want.lower() not in head.lower():
            raise Closed("handshake: bad accept")

    def settimeout(self, t):
        self.sock.settimeout(t)

    def _frame(self, op, data):
        n = len(data)
        head = bytes([0x80 | op])
        if n < 126:
            head += bytes([0x80 | n])
        elif n < 65536:
            head += bytes([0x80 | 126]) + struct.pack(">H", n)
        else:
            head += bytes([0x80 | 127]) + struct.pack(">Q", n)
        mask = os.urandom(4)
        body = _xor(data, mask)
        with self._send_lock:
            self.sock.sendall(head + mask + body)

    def send(self, text):
        if self.closed:
            raise Closed("closed")
        try:
            self._frame(1, text.encode("utf-8"))
        except OSError as e:
            self.closed = True
            raise Closed(str(e))

    def _fill(self):
        chunk = self.sock.recv(65536)   # socket.timeout هنا ما يضيّع شي (ما نشيل من البفر إلا فريم كامل)
        if not chunk:
            self.closed = True
            raise Closed("connection closed")
        self._buf += chunk

    def _frame_from_buf(self):
        b = self._buf
        if len(b) < 2:
            return None
        n, off = b[1] & 127, 2
        if n == 126:
            if len(b) < 4:
                return None
            n, off = struct.unpack(">H", b[2:4])[0], 4
        elif n == 127:
            if len(b) < 10:
                return None
            n, off = struct.unpack(">Q", b[2:10])[0], 10
        mask = None
        if b[1] & 0x80:
            if len(b) < off + 4:
                return None
            mask, off = b[off:off + 4], off + 4
        if len(b) < off + n:
            return None
        data = b[off:off + n]
        self._buf = b[off + n:]
        return b[0] & 0x80, b[0] & 15, (_xor(data, mask) if mask else data)

    def recv(self):
        """يرجع رسالة نصية. socket.timeout لو ما وصل شي، Closed لو انقطع."""
        while True:
            f = self._frame_from_buf()
            if f is None:
                self._fill()
                continue
            fin, op, data = f
            if op == 8:
                self.closed = True
                try:
                    self._frame(8, b"")
                except OSError:
                    pass
                raise Closed("closed by server")
            if op == 9:
                try:
                    self._frame(10, data)
                except OSError:
                    pass
                continue
            if op == 10:
                continue
            self._parts.append(data)
            if fin:
                out, self._parts = b"".join(self._parts), []
                return out.decode("utf-8", "replace")

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self._frame(8, b"")
            except OSError:
                pass
        try:
            self.sock.close()
        except OSError:
            pass


def _xor(data, mask):
    m = (mask * (len(data) // 4 + 1))[:len(data)]
    return (int.from_bytes(data, "big") ^ int.from_bytes(m, "big")).to_bytes(len(data), "big") if data else b""
