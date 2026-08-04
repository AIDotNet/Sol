-- 0003__ai_config.sql
-- AI provider / model / MCP configuration.
--
-- Everything here hangs off device_id rather than visitor_id, and that is deliberate.
-- A visitor can be assembled from a ProbabilisticCoarse guess at confidence 0.6, which
-- docs/device-identification.md is explicit must never gate access to anything. Keying API
-- keys on visitor_id would let one office behind a shared NAT read another person's
-- credentials. device_id only ever comes from the deterministic cookie/localStorage path.

CREATE TABLE IF NOT EXISTS ai_provider (
    provider_id   uuid        PRIMARY KEY,
    device_id     uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    -- Set when this row was created from a built-in preset; NULL for user-defined providers.
    -- Drives logo lookup and blocks deletion of built-ins in the UI.
    builtin_id    text        NULL,
    name          text        NOT NULL,
    description   text        NULL,
    -- User-supplied data URL overriding the preset logo.
    icon          text        NULL,
    -- Wire protocol. text + CHECK rather than a PG enum: adding a protocol is then a plain
    -- migration, and NpgsqlSlimDataSourceBuilder needs no composite type mapping under AOT.
    type          text        NOT NULL CHECK (type IN (
                      'openai-chat', 'openai-responses', 'anthropic', 'gemini',
                      'openai-images', 'openai-video', 'seedance-video', 'xai-video')),
    -- AES-GCM. The nonce must never repeat under one key, so it is generated per write and
    -- stored alongside; the tag is what makes the ciphertext tamper-evident.
    api_key_cipher bytea      NULL,
    api_key_nonce  bytea      NULL,
    api_key_tag    bytea      NULL,
    -- Last few characters, kept in clear for recognition. Never enough to reconstruct a key.
    api_key_hint  text        NULL,
    base_url      text        NOT NULL,
    enabled       boolean     NOT NULL DEFAULT true,
    -- Which preset version produced this row, so a later app version can upgrade built-in
    -- defaults without discarding the user's own edits.
    preset_version integer    NULL,
    sort_order    integer     NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),

    -- Import-by-URL is upsert-by-name, and a device listing two providers with the same
    -- name would make that ambiguous.
    CONSTRAINT uq_ai_provider_device_name UNIQUE (device_id, name)
);

CREATE TABLE IF NOT EXISTS ai_model (
    model_id      uuid        PRIMARY KEY,
    provider_id   uuid        NOT NULL REFERENCES ai_provider(provider_id) ON DELETE CASCADE,
    -- The identifier sent upstream (e.g. gpt-image-1). Distinct from model_id, which is ours.
    model_key     text        NOT NULL,
    name          text        NOT NULL,
    enabled       boolean     NOT NULL DEFAULT true,
    -- Protocol override. NULL means inherit the provider's, which is how one aggregator
    -- provider can serve models speaking three different protocols.
    type          text        NULL CHECK (type IS NULL OR type IN (
                      'openai-chat', 'openai-responses', 'anthropic', 'gemini',
                      'openai-images', 'openai-video', 'seedance-video', 'xai-video')),
    category      text        NOT NULL DEFAULT 'chat'
                              CHECK (category IN ('chat', 'image', 'video', 'embedding', 'speech')),
    icon          text        NULL,
    context_length    integer NULL,
    max_output_tokens integer NULL,
    supports_vision        boolean NOT NULL DEFAULT false,
    supports_function_call boolean NOT NULL DEFAULT false,
    supports_thinking      boolean NOT NULL DEFAULT false,
    sort_order    integer     NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_ai_model_provider_key UNIQUE (provider_id, model_key)
);

CREATE TABLE IF NOT EXISTS mcp_server (
    server_id     uuid        PRIMARY KEY,
    device_id     uuid        NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    name          text        NOT NULL,
    description   text        NULL,
    enabled       boolean     NOT NULL DEFAULT true,
    transport     text        NOT NULL CHECK (transport IN ('stdio', 'sse', 'streamable-http')),
    -- stdio transport
    command       text        NULL,
    args          jsonb       NOT NULL DEFAULT '[]'::jsonb,
    env           jsonb       NOT NULL DEFAULT '{}'::jsonb,
    cwd           text        NULL,
    -- http transports
    url           text        NULL,
    headers       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    -- Retry over SSE when a Streamable HTTP connection fails.
    auto_fallback boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_mcp_server_device_name UNIQUE (device_id, name)
);

-- Providers are always read as a full per-device list in display order.
CREATE INDEX IF NOT EXISTS ix_ai_provider_device
    ON ai_provider (device_id, sort_order, created_at);

CREATE INDEX IF NOT EXISTS ix_ai_model_provider
    ON ai_model (provider_id, sort_order);

CREATE INDEX IF NOT EXISTS ix_mcp_server_device
    ON mcp_server (device_id, created_at);
