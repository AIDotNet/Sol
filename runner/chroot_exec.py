#!/usr/bin/env python3
"""Fixed helper: enter one prepared job root, drop identity/capabilities, exec interpreter."""

from __future__ import annotations

import os
import resource
import sys


def main() -> None:
    if len(sys.argv) < 6:
        raise SystemExit("usage: chroot_exec.py ROOT UID TIMEOUT INTERPRETER SCRIPT [ARGS...]")
    root, uid_text, timeout_text, interpreter, script, *arguments = sys.argv[1:]
    uid = int(uid_text)
    timeout = int(timeout_text)
    if uid < 10000 or timeout < 1 or timeout > 120:
        raise SystemExit("invalid sandbox identity or timeout")

    resource.setrlimit(resource.RLIMIT_CPU, (timeout + 1, timeout + 2))
    resource.setrlimit(resource.RLIMIT_AS, (1610612736, 1610612736))
    resource.setrlimit(resource.RLIMIT_FSIZE, (16777216, 16777216))
    resource.setrlimit(resource.RLIMIT_NOFILE, (64, 64))
    resource.setrlimit(resource.RLIMIT_NPROC, (32, 32))

    os.chroot(root)
    os.chdir("/work")
    os.setgroups([])
    os.setgid(uid)
    os.setuid(uid)
    # Linux clears effective/permitted capabilities when uid changes from 0. The container also
    # enforces no-new-privileges and the jail contains no setuid executables.
    environment = {"HOME": "/work", "PATH": "/usr/bin:/bin", "LANG": "C.UTF-8"}
    os.execve(interpreter, [interpreter, script, *arguments], environment)


if __name__ == "__main__":
    main()
