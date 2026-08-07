-- Durable binary payloads for media and uploaded Skills.
--
-- Metadata remains in canvas_asset/skill so existing ownership and query paths stay intact.
-- Payload keys are opaque and are only resolved after the metadata row has passed the current
-- device/account scope check.

CREATE TABLE IF NOT EXISTS sol_asset_blob (
    storage_path text PRIMARY KEY,
    media_type   text NOT NULL,
    payload      bytea NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sol_skill_file (
    storage_path  text NOT NULL,
    relative_path text NOT NULL,
    payload       bytea NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (storage_path, relative_path)
);

CREATE INDEX IF NOT EXISTS ix_skill_file_storage_path
    ON sol_skill_file (storage_path);
