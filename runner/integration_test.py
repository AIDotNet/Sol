#!/usr/bin/env python3
"""Live isolation checks run inside the runner container against its UDS API."""

from __future__ import annotations

import base64
import http.client
import json
import os
import socket
import sys
from typing import Any

SOCKET_PATH = os.environ.get("SOL_RUNNER_SOCKET", "/run/sol/runner.sock")


class UnixConnection(http.client.HTTPConnection):
    def connect(self) -> None:
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(SOCKET_PATH)


def run(
    path: str,
    source: str,
    *,
    arguments: list[str] | None = None,
    stdin: str | None = None,
    timeout_ms: int = 5_000,
    max_output: int = 16_384,
) -> dict[str, Any]:
    payload = {
        "scriptPath": path,
        "arguments": arguments or [],
        "stdin": stdin,
        "timeoutMs": timeout_ms,
        "maxOutputBytes": max_output,
        "files": [
            {
                "path": path,
                "contentBase64": base64.b64encode(source.encode("utf-8")).decode("ascii"),
            }
        ],
    }
    connection = UnixConnection("skill-runner", timeout=10)
    connection.request(
        "POST",
        "/run",
        body=json.dumps(payload, separators=(",", ":")).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    response = connection.getresponse()
    raw = response.read()
    connection.close()
    if response.status != 200:
        raise AssertionError(f"runner returned HTTP {response.status}: {raw!r}")
    return json.loads(raw)


def expect(name: str, condition: bool, result: Any) -> None:
    if not condition:
        raise AssertionError(f"{name} failed: {result!r}")
    print(f"PASS {name}")


def main() -> None:
    python = run(
        "scripts/check.py",
        """
import os, pathlib, sys
text = sys.stdin.read().strip()
assert text == "input"
assert sys.argv[1:] == ["one", "two"]
assert not any(key.startswith("SOL_") for key in os.environ)
assert not pathlib.Path("/run/sol/runner.sock").exists()
assert not pathlib.Path("/jobs").exists()
assert not pathlib.Path("/app/runner.py").exists()
print("python-ok")
""",
        arguments=["one", "two"],
        stdin="input",
    )
    expect("python isolation", python["status"] == "succeeded" and python["stdout"].strip() == "python-ok", python)

    shell = run("check.sh", "test \"$1\" = hello && printf shell-ok", arguments=["hello"])
    expect("fixed shell interpreter", shell["status"] == "succeeded" and shell["stdout"] == "shell-ok", shell)

    node = run("check.js", "process.stdout.write(process.argv[2] === 'hello' ? 'node-ok' : 'bad')", arguments=["hello"])
    expect("fixed node interpreter", node["status"] == "succeeded" and node["stdout"] == "node-ok", node)

    network = run(
        "network.py",
        """
import pathlib
assert not pathlib.Path("/etc/resolv.conf").exists()
assert not pathlib.Path("/proc/net/route").exists()
print("network-namespace-hidden")
""",
    )
    expect(
        "network configuration hidden",
        network["status"] == "succeeded" and network["stdout"].strip() == "network-namespace-hidden",
        network,
    )

    timeout = run("loop.py", "while True: pass", timeout_ms=1_000)
    expect("process-group timeout", timeout["status"] == "timed_out", timeout)

    output = run("output.py", "while True: print('x' * 4096, flush=True)")
    expect(
        "output limit",
        output["status"] == "output_limit_exceeded" and output["stdoutTruncated"],
        output,
    )

    print("All live Skill runner isolation checks passed.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exception:
        print(f"FAIL {exception}", file=sys.stderr)
        raise
