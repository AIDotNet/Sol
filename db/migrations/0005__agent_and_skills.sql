-- 0005__agent_and_skills.sql
-- Durable canvas-agent conversations, runs, replayable events, and skill metadata.
--
-- Ownership is denormalized onto every mutable row. Repository queries always include device_id in
-- their predicates, so a guessed id cannot reveal whether another device owns the resource.

CREATE TABLE IF NOT EXISTS agent_session (
    session_id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    canvas_id uuid NOT NULL REFERENCES canvas(canvas_id) ON DELETE CASCADE,
    title text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_agent_session_device_canvas UNIQUE (device_id, canvas_id)
);

CREATE TABLE IF NOT EXISTS agent_run (
    run_id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES agent_session(session_id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    canvas_id uuid NOT NULL REFERENCES canvas(canvas_id) ON DELETE CASCADE,
    provider_id uuid NULL REFERENCES ai_provider(provider_id) ON DELETE SET NULL,
    model_key text NOT NULL,
    status text NOT NULL DEFAULT 'queued'
        CHECK (status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval',
                          'succeeded', 'failed', 'cancelled', 'interrupted')),
    executor_connection_id text NULL,
    iteration smallint NOT NULL DEFAULT 0,
    last_seq bigint NOT NULL DEFAULT 0,
    error text NULL,
    input_tokens integer NULL,
    output_tokens integer NULL,
    started_at timestamptz NULL,
    finished_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS agent_message (
    message_id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES agent_session(session_id) ON DELETE CASCADE,
    run_id uuid NULL REFERENCES agent_run(run_id) ON DELETE SET NULL,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    ordinal integer NOT NULL,
    role text NOT NULL CHECK (role IN ('user', 'assistant', 'tool', 'system')),
    content jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_agent_message_session_ordinal UNIQUE (session_id, ordinal)
);

CREATE TABLE IF NOT EXISTS agent_tool_call (
    tool_call_id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES agent_run(run_id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    tool_use_id text NOT NULL,
    tool_name text NOT NULL,
    site text NOT NULL CHECK (site IN ('canvas', 'server')),
    status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'awaiting_approval', 'running', 'succeeded',
                          'failed', 'denied', 'timeout')),
    input jsonb NOT NULL DEFAULT '{}'::jsonb,
    result jsonb NULL,
    error text NULL,
    started_at timestamptz NULL,
    finished_at timestamptz NULL,
    CONSTRAINT uq_agent_tool_call_run_use UNIQUE (run_id, tool_use_id)
);

CREATE TABLE IF NOT EXISTS agent_event (
    event_id bigserial PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES agent_run(run_id) ON DELETE CASCADE,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    seq bigint NOT NULL,
    type text NOT NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_agent_event_run_seq UNIQUE (run_id, seq)
);

CREATE TABLE IF NOT EXISTS skill (
    skill_id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    slug text NOT NULL,
    name text NOT NULL,
    description text NOT NULL DEFAULT '',
    storage_path text NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    has_scripts boolean NOT NULL DEFAULT false,
    risk jsonb NOT NULL DEFAULT '[]'::jsonb,
    max_risk text NOT NULL DEFAULT 'safe' CHECK (max_risk IN ('safe', 'warning', 'danger')),
    byte_size bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_skill_device_slug UNIQUE (device_id, slug)
);

CREATE INDEX IF NOT EXISTS ix_agent_session_device
    ON agent_session (device_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_agent_run_device
    ON agent_run (device_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_agent_run_canvas
    ON agent_run (canvas_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_agent_run_pending
    ON agent_run (status, updated_at)
    WHERE status IN ('queued', 'running', 'awaiting_canvas', 'awaiting_approval');
CREATE INDEX IF NOT EXISTS ix_agent_message_session
    ON agent_message (session_id, ordinal);
CREATE INDEX IF NOT EXISTS ix_agent_event_run
    ON agent_event (run_id, seq);
CREATE INDEX IF NOT EXISTS ix_agent_tool_call_run
    ON agent_tool_call (run_id);
CREATE INDEX IF NOT EXISTS ix_skill_device
    ON skill (device_id, name);
