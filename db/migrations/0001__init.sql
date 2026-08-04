-- 0001__init.sql
-- Migration bookkeeping. Applied by Sol.Infrastructure's MigrationRunner under a
-- PostgreSQL advisory lock, so concurrent instances cannot race on DDL.

CREATE TABLE IF NOT EXISTS schema_version (
    version    integer     PRIMARY KEY,
    name       text        NOT NULL,
    -- SHA-256 of the script as applied. Re-verified on every startup: editing a
    -- migration that has already shipped is a bug, and should stop the app loudly
    -- rather than leave environments silently divergent.
    checksum   bytea       NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);
