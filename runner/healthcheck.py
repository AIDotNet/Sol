#!/usr/bin/env python3
import os
import socket
import sys

path = os.environ.get("SOL_RUNNER_SOCKET", "/run/sol/runner.sock")
try:
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    client.settimeout(3)
    client.connect(path)
    client.sendall(b"GET /health HTTP/1.1\r\nHost: skill-runner\r\nConnection: close\r\n\r\n")
    response = client.recv(128)
    sys.exit(0 if b" 200 " in response.split(b"\r\n", 1)[0] else 1)
except OSError:
    sys.exit(1)
finally:
    try:
        client.close()
    except NameError:
        pass
