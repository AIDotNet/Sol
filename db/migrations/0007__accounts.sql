-- Account identity and provider-neutral OAuth.
--
-- Guest data remains owned by device_id. account_device is the durable bridge that lets a
-- signed-in account read every device it has explicitly claimed. The API sets the account scope
-- on each pooled connection only after validating the session cookie and device membership.

CREATE TABLE IF NOT EXISTS sol_account (
    account_id   uuid PRIMARY KEY,
    display_name text NOT NULL,
    email        text NULL,
    avatar_url   text NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS sol_external_identity (
    external_identity_id uuid PRIMARY KEY,
    account_id           uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    provider_key         text NOT NULL,
    subject              text NOT NULL,
    display_name         text NOT NULL,
    email                text NULL,
    avatar_url           text NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    last_login_at        timestamptz NOT NULL DEFAULT now(),

    -- The provider subject is the only stable key. Email is profile data and is deliberately not
    -- used for implicit account merging because it is not equally verified by every provider.
    CONSTRAINT uq_external_identity_provider_subject UNIQUE (provider_key, subject),
    CONSTRAINT uq_external_identity_account_provider UNIQUE (account_id, provider_key)
);

CREATE TABLE IF NOT EXISTS sol_account_device (
    account_id uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    device_id  uuid NOT NULL REFERENCES device(device_id) ON DELETE CASCADE,
    linked_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, device_id),
    CONSTRAINT uq_account_device_device UNIQUE (device_id)
);

CREATE TABLE IF NOT EXISTS sol_auth_session (
    session_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    -- SHA-256 of the random HttpOnly cookie value. The raw value never enters the database.
    token_hash  bytea NOT NULL UNIQUE,
    created_at  timestamptz NOT NULL,
    expires_at  timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    user_agent  text NULL,
    ip_address  inet NULL
);

CREATE TABLE IF NOT EXISTS sol_oauth_transaction (
    transaction_id uuid PRIMARY KEY,
    state_hash     bytea NOT NULL UNIQUE,
    provider_key   text NOT NULL,
    link_account_id uuid NULL REFERENCES sol_account(account_id) ON DELETE CASCADE,
    device_id      uuid NULL REFERENCES device(device_id) ON DELETE CASCADE,
    return_path    text NOT NULL,
    code_verifier  text NOT NULL,
    created_at     timestamptz NOT NULL,
    expires_at     timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_external_identity_account
    ON sol_external_identity (account_id, created_at);
CREATE INDEX IF NOT EXISTS ix_account_device_account
    ON sol_account_device (account_id, linked_at);
CREATE INDEX IF NOT EXISTS ix_auth_session_account
    ON sol_auth_session (account_id, expires_at);
CREATE INDEX IF NOT EXISTS ix_oauth_transaction_expiry
    ON sol_oauth_transaction (expires_at);

-- Repositories continue to accept DeviceId so guest behavior and all existing use cases remain
-- source-compatible. A request-scoped account id is set on the connection by the API only after
-- session + device validation. This function is intentionally not SECURITY DEFINER.
CREATE OR REPLACE FUNCTION sol_accessible_device_ids(request_device_id uuid)
RETURNS TABLE(device_id uuid)
LANGUAGE SQL
STABLE
AS $$
    SELECT request_device_id
    UNION
    SELECT ad.device_id
    FROM sol_account_device ad
    WHERE NULLIF(current_setting('sol.account_id', true), '') IS NOT NULL
      AND ad.account_id = NULLIF(current_setting('sol.account_id', true), '')::uuid
$$;
