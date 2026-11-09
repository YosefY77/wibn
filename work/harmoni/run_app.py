import threading
import time
import webbrowser

import server


def main():
    server._load_profiles()
    t = threading.Thread(
        target=lambda: server.socketio.run(
            server.app, host="0.0.0.0", port=server.PORT,
            debug=False, allow_unsafe_werkzeug=True,
        ),
        daemon=True,
    )
    t.start()
    time.sleep(1.5)
    webbrowser.open("http://localhost:%d" % server.PORT)
    while t.is_alive():
        time.sleep(1)


if __name__ == "__main__":
    main()
