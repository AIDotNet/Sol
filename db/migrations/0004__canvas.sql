-- 0004__canvas.sql
-- Canvas graphs, generated assets, and asynchronous video jobs.
--
-- Same ownership rule as 0003: everything hangs off device_id, never visitor_id, because a
-- visitor can be assembled from a 0.6-confidence guess.

CREATE TABLE IF NOT EXISTS canvas (
    canvas_id  uuid        PRIMARY KEY,
    device_id  uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    name       text        NOT NULL,
    -- The whole node/edge graph. Opaque to the server: it is produced and consumed by the
    -- client, and giving each node a row would buy nothing when the graph is always read and
    -- written whole.
    graph      jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS canvas_asset (
    asset_id    uuid        PRIMARY KEY,
    device_id   uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    kind        text        NOT NULL CHECK (kind IN ('image', 'video')),
    media_type  text        NOT NULL,
    -- Path relative to Ai:AssetRoot. Bytes live on disk: Postgres would have to TOAST
    -- multi-megabyte images, and streaming them back out of a large object is slower than
    -- serving a file.
    storage_path text       NOT NULL,
    byte_size   bigint      NOT NULL,
    -- The prompt that produced this, kept so a generated image fed back in as a reference
    -- still carries its description.
    prompt      text        NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS video_job (
    job_id         uuid        PRIMARY KEY,
    device_id      uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    provider_id    uuid        NULL REFERENCES ai_provider(provider_id) ON DELETE SET NULL,
    model_key      text        NOT NULL,
    -- Video generation runs for minutes, so the request outlives the HTTP call that started it.
    -- The client polls this row rather than holding a connection open.
    status         text        NOT NULL DEFAULT 'pending'
                               CHECK (status IN ('pending', 'running', 'succeeded', 'failed', 'cancelled')),
    -- The vendor's own job identifier, used to poll upstream.
    upstream_job_id text       NULL,
    progress       real        NULL CHECK (progress IS NULL OR (progress >= 0 AND progress <= 1)),
    asset_id       uuid        NULL REFERENCES canvas_asset(asset_id) ON DELETE SET NULL,
    error          text        NULL,
    -- Echoed back so a reload can restore what the job was asked to make.
    request        jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_canvas_device
    ON canvas (device_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_canvas_asset_device
    ON canvas_asset (device_id, created_at DESC);

-- Serves the background poller, which repeatedly asks for jobs that are not yet finished.
CREATE INDEX IF NOT EXISTS ix_video_job_pending
    ON video_job (status, updated_at)
    WHERE status IN ('pending', 'running');

CREATE INDEX IF NOT EXISTS ix_video_job_device
    ON video_job (device_id, created_at DESC);
