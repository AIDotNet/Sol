-- 0002__identity.sql
-- Device identity. See docs/device-identification.md for the reasoning behind the
-- two-fingerprint design and the deliberately reversible device_link edges.

CREATE TABLE IF NOT EXISTS visitor (
    visitor_id uuid        PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS device (
    device_id   uuid        PRIMARY KEY,
    -- Hash over all signals, including browser-dependent ones. Equal values mean
    -- the same browser profile.
    fp_exact    bytea       NOT NULL,
    -- Hash over browser-INDEPENDENT signals plus ip_prefix. The only fingerprint that
    -- can survive a browser switch, and weak: these signals carry ~10-15 bits of
    -- correlated entropy, so a match is evidence rather than proof.
    fp_coarse   bytea       NOT NULL,
    -- /24 for IPv4, /48 for IPv6. Narrower retention than a full address, and stable
    -- across a DHCP lease change.
    ip_prefix   inet        NOT NULL,
    signals     jsonb       NOT NULL,
    -- Which normalization scheme produced the hashes, so two schemes can coexist
    -- during a rollout instead of invalidating every row at once.
    sig_version integer     NOT NULL,
    user_agent  text        NULL,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    last_seen   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS device_link (
    device_id  uuid        NOT NULL REFERENCES device(device_id)   ON DELETE CASCADE,
    visitor_id uuid        NOT NULL REFERENCES visitor(visitor_id) ON DELETE CASCADE,
    confidence real        NOT NULL CHECK (confidence > 0 AND confidence <= 1),
    -- 0 = deterministic (cookie / stored id), 1 = probabilistic coarse, 2 = manual.
    -- smallint rather than a PG enum so NpgsqlSlimDataSourceBuilder needs no extra mapping.
    method     smallint    NOT NULL CHECK (method IN (0, 1, 2)),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (device_id, visitor_id)
);

-- Serves the fuzzy candidate lookup: equality on (fp_coarse, ip_prefix) with a
-- recency bound and a small row cap.
CREATE INDEX IF NOT EXISTS ix_device_coarse_ip_seen
    ON device (fp_coarse, ip_prefix, last_seen DESC);

CREATE INDEX IF NOT EXISTS ix_device_fp_exact
    ON device (fp_exact);

CREATE INDEX IF NOT EXISTS ix_device_link_visitor
    ON device_link (visitor_id);
