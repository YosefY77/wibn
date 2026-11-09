import server

server._PROFILES_FILE = __file__ + '.profiles.json'
server.PROFILES = {}
server.socketio.run(server.app, host='127.0.0.1', port=5079, debug=False, allow_unsafe_werkzeug=True)
