"""Speak the installed Blender MCP protocol on the task's isolated port."""
import json
import pathlib
import socket
import sys

request = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
with socket.create_connection(('127.0.0.1', 9877), timeout=15) as connection:
    connection.settimeout(240)
    connection.sendall(json.dumps(request, ensure_ascii=False).encode('utf-8'))
    data = b''
    while True:
        block = connection.recv(65536)
        if not block:
            raise RuntimeError('MCP disconnected before its response')
        data += block
        try:
            response = json.loads(data.decode('utf-8'))
            break
        except (json.JSONDecodeError, UnicodeDecodeError):
            pass
print(json.dumps(response, ensure_ascii=False, indent=2))
if response.get('status') != 'success':
    sys.exit(1)
