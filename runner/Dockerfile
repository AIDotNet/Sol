FROM node:24-bookworm-slim

RUN apt-get -o Acquire::Retries=10 -o Acquire::http::Timeout=60 update \
    && apt-get -o Acquire::Retries=10 -o Acquire::http::Timeout=60 install --yes --no-install-recommends bubblewrap python3 util-linux \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app /run/sol /jobs \
    && chown -R node:node /run/sol /jobs \
    && chmod 0700 /run/sol /jobs

COPY runner.py healthcheck.py integration_test.py chroot_exec.py /app/
RUN chmod 0555 /app/runner.py /app/healthcheck.py /app/integration_test.py /app/chroot_exec.py

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    SOL_RUNNER_SOCKET=/run/sol/runner.sock \
    SOL_RUNNER_SOCKET_MODE=0660 \
    SOL_RUNNER_JOBS=/jobs

USER node

ENTRYPOINT ["/usr/bin/python3", "/app/runner.py"]
