-- 0006__canvas_asset_groups.sql
-- User-created folders for the device-scoped canvas asset library.

CREATE TABLE IF NOT EXISTS canvas_asset_group (
    group_id    uuid        PRIMARY KEY,
    device_id   uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    name        text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- Names are unique per device without making the UI care about case. The explicit index also
-- works for databases that already applied 0004 before this migration was introduced.
CREATE UNIQUE INDEX IF NOT EXISTS ux_canvas_asset_group_device_name
    ON canvas_asset_group (device_id, lower(name));

ALTER TABLE canvas_asset
    ADD COLUMN IF NOT EXISTS group_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_canvas_asset_group'
          AND conrelid = 'canvas_asset'::regclass
    ) THEN
        ALTER TABLE canvas_asset
            ADD CONSTRAINT fk_canvas_asset_group
            FOREIGN KEY (group_id)
            REFERENCES canvas_asset_group(group_id)
            ON DELETE SET NULL;
    END IF;
END
$$;

CREATE INDEX IF NOT EXISTS ix_canvas_asset_group
    ON canvas_asset (device_id, group_id, created_at DESC);
