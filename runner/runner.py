#!/usr/bin/env python3
"""Sol Skill runner: UDS-only control plane and per-job bubblewrap sandbox."""

from __future__ import annotations

import base64
import binascii
import http.server
import itertools
import json
import os
import pathlib
import shutil
import signal
import socketserver
import subprocess
import tempfile
import threading
import time
from typing import Any

SOCKET_PATH = pathlib.Path(os.environ.get("SOL_RUNNER_SOCKET", "/run/sol/runner.sock"))
SOCKET_MODE = int(os.environ.get("SOL_RUNNER_SOCKET_MODE", "0660"), 8)
JOBS_ROOT = pathlib.Path(os.environ.get("SOL_RUNNER_JOBS", "/jobs"))
MAX_REQUEST_BYTES = int(os.environ.get("SOL_RUNNER_MAX_REQUEST_BYTES", str(40 * 1024 * 1024)))
MAX_FILES = int(os.environ.get("SOL_RUNNER_MAX_FILES", "1000"))
MAX_PACKAGE_BYTES = int(os.environ.get("SOL_RUNNER_MAX_PACKAGE_BYTES", str(25 * 1024 * 1024)))
MAX_STDIN_BYTES = 256 * 1024
MAX_ARGUMENTS = 64
MAX_TIMEOUT_MS = 120_000
RUN_SLOTS = threading.BoundedSemaphore(int(os.environ.get("SOL_RUNNER_MAX_CONCURRENT", "2")))
JOB_UIDS = itertools.cycle(range(20000, 21000))
JOB_UID_LOCK = threading.Lock()
BWRAP = "/usr/bin/bwrap"
PRLIMIT = "/usr/bin/prlimit"
CHROOT_HELPER = "/app/chroot_exec.py"
SANDBOX_ROOT = JOBS_ROOT / ".sandbox-root"
INTERPRETERS = {
    ".sh": "/bin/sh",
    ".py": "/usr/bin/python3",
    ".js": "/usr/local/bin/node",
}


def safe_relative_path(value: Any) -> pathlib.PurePosixPath:
    if not isinstance(value, str) or not value or "\\" in value or value.startswith("/"):
        raise ValueError("invalid relative path")
    raw_parts = value.split("/")
    if any(part in ("", ".", "..") or len(part) > 255 for part in raw_parts):
        raise ValueError("unsafe relative path")
    if len(value) > 512 or len(value.encode("utf-8")) > 1024:
        raise ValueError("relative path exceeds length limit")
    return pathlib.PurePosixPath(value)


def decoded_files(value: Any) -> list[tuple[pathlib.PurePosixPath, bytes]]:
    if not isinstance(value, list) or not 0 < len(value) <= MAX_FILES:
        raise ValueError("invalid file count")
    files: list[tuple[pathlib.PurePosixPath, bytes]] = []
    seen: set[str] = set()
    total = 0
    for item in value:
        if not isinstance(item, dict):
            raise ValueError("invalid file entry")
        path = safe_relative_path(item.get("path"))
        key = str(path)
        if key in seen:
            raise ValueError("duplicate file path")
        seen.add(key)
        encoded = item.get("contentBase64")
        if not isinstance(encoded, str):
            raise ValueError("missing file content")
        try:
            content = base64.b64decode(encoded, validate=True)
        except (binascii.Error, ValueError) as exception:
            raise ValueError("invalid base64 file content") from exception
        total += len(content)
        if total > MAX_PACKAGE_BYTES:
            raise ValueError("package exceeds execution size limit")
        files.append((path, content))
    return files


def write_package(root: pathlib.Path, files: list[tuple[pathlib.PurePosixPath, bytes]]) -> None:
    root.mkdir(mode=0o700)
    root_real = root.resolve()
    for relative, content in files:
        target = root.joinpath(*relative.parts)
        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if root_real not in target.resolve().parents:
            raise ValueError("file path escaped package directory")
        with target.open("xb") as output:
            output.write(content)
        target.chmod(0o400)


def copy_into_root(source: pathlib.Path, root: pathlib.Path) -> None:
    target = root / source.relative_to("/")
    target.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
    shutil.copy2(source.resolve(), target)


def library_paths(executable: pathlib.Path) -> list[pathlib.Path]:
    result = subprocess.run(
        ["/usr/bin/ldd", str(executable)],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        timeout=5,
        check=True,
    )
    paths: list[pathlib.Path] = []
    for token in result.stdout.replace("=>", " ").split():
        if token.startswith("/") and os.path.isfile(token):
            paths.append(pathlib.Path(token))
    return paths


def initialize_chroot_runtime() -> None:
    if SANDBOX_ROOT.exists():
        return
    staging = JOBS_ROOT / ".sandbox-root.initializing"
    shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(mode=0o755)
    executables = [pathlib.Path(path) for path in INTERPRETERS.values()]
    for executable in executables:
        copy_into_root(executable, staging)
        for library in library_paths(executable):
            copy_into_root(library, staging)
    python_stdlib = pathlib.Path("/usr/lib/python3.11")
    if python_stdlib.is_dir():
        shutil.copytree(
            python_stdlib,
            staging / "usr/lib/python3.11",
            ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "test", "tests"),
        )
    seal_read_only(staging)
    executable_paths = [pathlib.Path(path) for path in INTERPRETERS.values()]
    for interpreter in executables:
        executable_paths.extend(library_paths(interpreter))
    for executable in executable_paths:
        (staging / str(executable).removeprefix("/")).chmod(0o555)
    staging.rename(SANDBOX_ROOT)


def seal_read_only(root: pathlib.Path) -> None:
    for path in root.rglob("*"):
        path.chmod(0o555 if path.is_dir() else 0o444)
    root.chmod(0o555)


def bwrap_command(
    skill_root: pathlib.Path,
    work_root: pathlib.Path,
    interpreter: str,
    script_path: pathlib.PurePosixPath,
    arguments: list[str],
    timeout_seconds: int = 30,
) -> list[str]:
    return [
        PRLIMIT,
        f"--cpu={timeout_seconds + 1}:{timeout_seconds + 2}",
        "--as=1610612736:1610612736",
        "--fsize=16777216:16777216",
        "--nofile=64:64",
        "--nproc=32:32",
        "--",
        BWRAP,
        "--die-with-parent",
        "--new-session",
        "--unshare-user",
        "--unshare-pid",
        "--unshare-net",
        "--unshare-ipc",
        "--unshare-uts",
        "--unshare-cgroup-try",
        "--uid", "0",
        "--gid", "0",
        "--cap-drop", "ALL",
        "--ro-bind", "/usr", "/usr",
        "--ro-bind", "/usr/local", "/usr/local",
        "--symlink", "usr/bin", "/bin",
        "--symlink", "usr/lib", "/lib",
        "--symlink", "usr/lib64", "/lib64",
        "--dir", "/skill",
        "--ro-bind", str(skill_root), "/skill",
        "--dir", "/work",
        "--bind", str(work_root), "/work",
        "--proc", "/proc",
        "--dev", "/dev",
        "--tmpfs", "/tmp",
        "--chdir", "/work",
        "--clearenv",
        "--setenv", "HOME", "/work",
        "--setenv", "PATH", "/usr/bin:/bin",
        "--setenv", "LANG", "C.UTF-8",
        interpreter,
        "/skill/" + str(script_path),
        *arguments,
    ]


def bwrap_ready() -> bool:
    required = [BWRAP, PRLIMIT, "/bin/sh", "/usr/bin/python3", "/usr/local/bin/node"]
    if any(not os.path.isfile(path) or not os.access(path, os.X_OK) for path in required):
        return False
    try:
        probe = subprocess.run(
            [
                BWRAP,
                "--die-with-parent",
                "--unshare-user",
                "--unshare-pid",
                "--unshare-net",
                "--uid", "0",
                "--gid", "0",
                "--cap-drop", "ALL",
                "--ro-bind", "/usr", "/usr",
                "--symlink", "usr/bin", "/bin",
                "/usr/bin/true",
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=2,
            check=False,
        )
        return probe.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def chroot_ready() -> bool:
    required = [
        CHROOT_HELPER,
        SANDBOX_ROOT / "bin/sh",
        SANDBOX_ROOT / "usr/bin/python3",
        SANDBOX_ROOT / "usr/local/bin/node",
    ]
    return os.geteuid() == 0 and all(os.path.isfile(path) for path in required)


def sandbox_ready() -> bool:
    return bwrap_ready() or chroot_ready()


def next_job_uid() -> int:
    with JOB_UID_LOCK:
        return next(JOB_UIDS)


def prepare_chroot(job_root: pathlib.Path, uid: int) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
    jail = job_root / "root"
    shutil.copytree(SANDBOX_ROOT, jail, symlinks=False)
    skill_root = jail / "skill"
    work_root = jail / "work"
    tmp_root = jail / "tmp"
    work_root.mkdir(mode=0o700)
    tmp_root.mkdir(mode=0o700)
    os.chown(work_root, uid, uid)
    os.chown(tmp_root, uid, uid)
    return jail, skill_root, work_root


def chroot_command(
    jail: pathlib.Path,
    uid: int,
    interpreter: str,
    script_path: pathlib.PurePosixPath,
    arguments: list[str],
    timeout_seconds: int,
) -> list[str]:
    return [
        "/usr/bin/python3",
        CHROOT_HELPER,
        str(jail),
        str(uid),
        str(timeout_seconds),
        interpreter,
        "/skill/" + str(script_path),
        *arguments,
    ]


def terminate_group(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def read_limited(
    stream: Any,
    limit: int,
    output: bytearray,
    truncated: list[bool],
    overflow: threading.Event,
) -> None:
    try:
        while True:
            chunk = stream.read(16 * 1024)
            if not chunk:
                return
            remaining = limit - len(output)
            if remaining > 0:
                output.extend(chunk[:remaining])
            if len(chunk) > remaining:
                truncated[0] = True
                overflow.set()
                return
    finally:
        stream.close()


def run_script(request: dict[str, Any]) -> dict[str, Any]:
    files = decoded_files(request.get("files"))
    script_path = safe_relative_path(request.get("scriptPath"))
    extension = pathlib.PurePosixPath(script_path).suffix.lower()
    interpreter = INTERPRETERS.get(extension)
    if interpreter is None or str(script_path) not in {str(path) for path, _ in files}:
        raise ValueError("script is missing or has an unsupported extension")

    arguments = request.get("arguments", [])
    if not isinstance(arguments, list) or len(arguments) > MAX_ARGUMENTS:
        raise ValueError("invalid script arguments")
    if any(not isinstance(argument, str) or len(argument) > 4096 for argument in arguments):
        raise ValueError("invalid script argument")

    stdin = request.get("stdin")
    if stdin is not None and not isinstance(stdin, str):
        raise ValueError("stdin must be a string")
    stdin_bytes = (stdin or "").encode("utf-8")
    if len(stdin_bytes) > MAX_STDIN_BYTES:
        raise ValueError("stdin exceeds 256 KiB")

    timeout_ms = request.get("timeoutMs", 30_000)
    max_output = request.get("maxOutputBytes", 256 * 1024)
    if not isinstance(timeout_ms, int) or not 1_000 <= timeout_ms <= MAX_TIMEOUT_MS:
        raise ValueError("invalid timeout")
    if not isinstance(max_output, int) or not 16_384 <= max_output <= 1024 * 1024:
        raise ValueError("invalid output limit")

    job_root = pathlib.Path(tempfile.mkdtemp(prefix="job-", dir=JOBS_ROOT))
    try:
        timeout_seconds = max(1, (timeout_ms + 999) // 1000)
        if bwrap_ready():
            skill_root = job_root / "skill"
            work_root = job_root / "work"
            write_package(skill_root, files)
            work_root.mkdir(mode=0o700)
            command = bwrap_command(
                skill_root, work_root, interpreter, script_path, arguments, timeout_seconds)
        elif chroot_ready():
            uid = next_job_uid()
            jail, skill_root, _ = prepare_chroot(job_root, uid)
            write_package(skill_root, files)
            seal_read_only(skill_root)
            command = chroot_command(
                jail, uid, interpreter, script_path, arguments, timeout_seconds)
        else:
            return {
                "status": "sandbox_unavailable",
                "exitCode": None,
                "stdout": "",
                "stderr": "No supported sandbox backend is available.",
                "stdoutTruncated": False,
                "stderrTruncated": False,
            }
        started = time.monotonic()
        try:
            process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                start_new_session=True,
            )
        except OSError as exception:
            return {
                "status": "sandbox_unavailable",
                "exitCode": None,
                "stdout": "",
                "stderr": str(exception)[:max_output],
                "stdoutTruncated": False,
                "stderrTruncated": len(str(exception).encode("utf-8")) > max_output,
            }

        stdout = bytearray()
        stderr = bytearray()
        stdout_truncated = [False]
        stderr_truncated = [False]
        overflow = threading.Event()
        stdout_thread = threading.Thread(
            target=read_limited,
            args=(process.stdout, max_output, stdout, stdout_truncated, overflow),
            daemon=True,
        )
        stderr_thread = threading.Thread(
            target=read_limited,
            args=(process.stderr, max_output, stderr, stderr_truncated, overflow),
            daemon=True,
        )
        stdout_thread.start()
        stderr_thread.start()
        assert process.stdin is not None

        def write_stdin() -> None:
            try:
                process.stdin.write(stdin_bytes)
                process.stdin.close()
            except (BrokenPipeError, OSError):
                pass

        stdin_thread = threading.Thread(target=write_stdin, daemon=True)
        stdin_thread.start()

        status = "failed"
        deadline = started + timeout_ms / 1000
        while process.poll() is None:
            if overflow.is_set():
                status = "output_limit_exceeded"
                terminate_group(process)
                break
            if time.monotonic() >= deadline:
                status = "timed_out"
                terminate_group(process)
                break
            time.sleep(0.01)
        process.wait(timeout=5)
        stdout_thread.join(timeout=2)
        stderr_thread.join(timeout=2)
        stdin_thread.join(timeout=2)
        if status != "timed_out" and overflow.is_set():
            # The reader closes its pipe at the byte cap. A fast writer can observe BrokenPipe and
            # exit before the supervisor loop sees the event; the terminal state is still output
            # exhaustion, not an ordinary script failure.
            status = "output_limit_exceeded"
        elif status not in ("timed_out", "output_limit_exceeded"):
            status = "succeeded" if process.returncode == 0 else "failed"

        return {
            "status": status,
            "exitCode": process.returncode,
            "stdout": stdout.decode("utf-8", errors="replace"),
            "stderr": stderr.decode("utf-8", errors="replace"),
            "stdoutTruncated": stdout_truncated[0],
            "stderrTruncated": stderr_truncated[0],
        }
    finally:
        shutil.rmtree(job_root, ignore_errors=True)


class UnixHttpServer(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True
    allow_reuse_address = True


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "SolSkillRunner/1"

    def do_GET(self) -> None:
        if self.path != "/health":
            self.send_error(404)
            return
        ready = sandbox_ready()
        self.send_json(200 if ready else 503, {"ok": ready})

    def do_POST(self) -> None:
        if self.path != "/run":
            self.send_error(404)
            return
        if self.headers.get("Transfer-Encoding"):
            self.send_json(400, {"error": "transfer encoding is not accepted"})
            return
        try:
            length = int(self.headers.get("Content-Length", "-1"))
        except ValueError:
            length = -1
        if length < 0 or length > MAX_REQUEST_BYTES:
            self.send_json(413, {"error": "request exceeds size limit"})
            return
        try:
            raw = self.rfile.read(length)
            request = json.loads(raw)
            if not isinstance(request, dict):
                raise ValueError("request must be an object")
            if not RUN_SLOTS.acquire(blocking=False):
                self.send_json(429, {"error": "runner_busy"})
                return
            try:
                result = run_script(request)
            finally:
                RUN_SLOTS.release()
            self.send_json(200, result)
        except (ValueError, json.JSONDecodeError) as exception:
            self.send_json(400, {"error": str(exception)[:500]})
        except Exception:
            self.send_json(500, {"error": "runner_internal_error"})

    def send_json(self, status: int, value: dict[str, Any]) -> None:
        body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: Any) -> None:
        print(f"runner: {format % args}", flush=True)


def main() -> None:
    JOBS_ROOT.mkdir(mode=0o700, parents=True, exist_ok=True)
    if os.geteuid() == 0:
        initialize_chroot_runtime()
    SOCKET_PATH.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    try:
        SOCKET_PATH.unlink()
    except FileNotFoundError:
        pass
    except OSError:
        # Docker Desktop's VirtioFS can create a socket but cannot stat/chmod it from the
        # container. A stale unsupported entry still makes bind fail below, which is safer than
        # deleting an arbitrary path.
        pass
    server = UnixHttpServer(str(SOCKET_PATH), Handler)
    try:
        SOCKET_PATH.chmod(SOCKET_MODE)
    except OSError:
        # Linux local volumes support chmod. Some macOS bind mounts return EINVAL for socket
        # metadata; the socket remains usable inside the container and API availability probing
        # decides whether the host can reach it.
        pass
    try:
        server.serve_forever(poll_interval=0.25)
    finally:
        server.server_close()
        try:
            SOCKET_PATH.unlink()
        except (FileNotFoundError, OSError):
            pass


if __name__ == "__main__":
    main()
