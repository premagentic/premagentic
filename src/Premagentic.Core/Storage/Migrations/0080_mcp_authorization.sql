-- The MCP authorization flow (OAuth 2.1 with PKCE), and what makes a disable
-- or a password change final for a person's assistant credentials.
--
-- credential_generation, on app_user and agent: bumped in the UPDATE that
-- writes a disable and, on app_user, in the one that sets a new password.
-- Never decremented. A credential that captured a generation is dead once the
-- current one differs, so re-enabling brings nothing back. A row disabled
-- when this runs starts at 1, so a disable already in force is final too.
--
-- agent_token.user_generation and agent_generation: stamped at issue for a
-- token of an agent its person made (created_by self:), NULL for any other
-- agent's token, which a disable or a password change leaves as it was.
-- Existing self: tokens are stamped (0, 0): one whose owner or agent is
-- disabled now is superseded, every other stays current.
--
-- oauth_client: a registered client. Its id is prem_cli_<24 hex>, or an https
-- URL an administrator typed for a client configured with that id.
-- application_type is kept only as native or web. model_location and
-- model_vendor are an administrator's statement, never a client's.
--
-- oauth_grant: one person's approval of one client, with the agent that acts
-- for it. Pending until its first code exchange sets activated_at. The
-- generations it captured must equal the current ones for it to be live.
--
-- oauth_code, oauth_refresh_token, oauth_access_token: only the SHA-256 of
-- each secret is stored. No row is deleted before its expires_at: ending a
-- grant marks the grant, and its tokens die with it.
--
-- None of the oauth_* tables is read or written while mcp.oauth.enabled is
-- false, and none is in the configuration export.

-- schema: prem_config

ALTER TABLE prem_config.app_user
    ADD COLUMN credential_generation BIGINT NOT NULL DEFAULT 0;
ALTER TABLE prem_config.agent
    ADD COLUMN credential_generation BIGINT NOT NULL DEFAULT 0;

UPDATE prem_config.app_user SET credential_generation = 1 WHERE disabled;
UPDATE prem_config.agent SET credential_generation = 1 WHERE disabled;

ALTER TABLE prem_config.agent_token
    ADD COLUMN user_generation BIGINT,
    ADD COLUMN agent_generation BIGINT,
    ADD CONSTRAINT agent_token_generations_together
        CHECK ((user_generation IS NULL) = (agent_generation IS NULL));

UPDATE prem_config.agent_token t
SET user_generation = 0, agent_generation = 0
FROM prem_config.agent a
WHERE a.tenant_id = t.tenant_id AND a.id = t.agent_id AND a.created_by LIKE 'self:%';

ALTER TABLE prem_config.agent
    DROP CONSTRAINT agent_created_by_shape,
    ADD CONSTRAINT agent_created_by_shape
        CHECK (created_by = 'unknown' OR created_by ~ '^(cli|portal|self|profile|oauth):.+$');

CREATE INDEX agent_oauth_made_by_idx
    ON prem_config.agent(tenant_id, created_by)
    WHERE created_by LIKE 'oauth:%' AND deleted_at IS NULL;

CREATE TABLE prem_config.oauth_client(
    id TEXT NOT NULL,
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    name TEXT NOT NULL,
    redirect_uris TEXT[] NOT NULL,
    application_type TEXT,
    software_id TEXT,
    software_version TEXT,
    registered_by TEXT NOT NULL,
    registered_from TEXT,
    model_location TEXT,
    model_vendor TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    first_approved_at TIMESTAMPTZ,
    disabled BOOLEAN NOT NULL DEFAULT false,
    deleted_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, id),
    CONSTRAINT oauth_client_id_shape
        CHECK (id ~ '^prem_cli_[0-9a-f]{24}$' OR (id ~ '^https://[!-~]+$' AND char_length(id) <= 2000)),
    CONSTRAINT oauth_client_name_trimmed
        CHECK (name <> '' AND name = btrim(name) AND char_length(name) <= 64),
    CONSTRAINT oauth_client_redirect_count CHECK (cardinality(redirect_uris) BETWEEN 1 AND 5),
    CONSTRAINT oauth_client_application_type
        CHECK (application_type IS NULL OR application_type IN ('native', 'web')),
    CONSTRAINT oauth_client_registered_by
        CHECK (registered_by = 'dynamic' OR registered_by ~ '^(cli|portal):.+$'),
    CONSTRAINT oauth_client_registered_from
        CHECK ((registered_by = 'dynamic') = (registered_from IS NOT NULL)),
    CONSTRAINT oauth_client_model_location
        CHECK (model_location IS NULL OR model_location IN ('local', 'hosted')),
    CONSTRAINT oauth_client_model_vendor
        CHECK (model_vendor IS NULL
               OR (model_location = 'hosted' AND model_vendor <> '' AND model_vendor = btrim(model_vendor)))
);
CREATE INDEX oauth_client_pending_from_idx
    ON prem_config.oauth_client(tenant_id, registered_from)
    WHERE registered_by = 'dynamic' AND first_approved_at IS NULL AND deleted_at IS NULL;

CREATE TABLE prem_config.oauth_grant(
    id TEXT PRIMARY KEY,
    tenant_id UUID NOT NULL,
    client_id TEXT NOT NULL,
    user_id UUID NOT NULL,
    agent_id UUID NOT NULL,
    user_generation BIGINT NOT NULL,
    agent_generation BIGINT NOT NULL,
    scope TEXT NOT NULL,
    resource TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    activated_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    revoked_reason TEXT,
    last_refreshed_at TIMESTAMPTZ,
    refresh_count INT NOT NULL DEFAULT 0,
    FOREIGN KEY (tenant_id, client_id) REFERENCES prem_config.oauth_client(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES prem_config.app_user(tenant_id, id),
    FOREIGN KEY (tenant_id, agent_id) REFERENCES prem_config.agent(tenant_id, id),
    CONSTRAINT oauth_grant_id_shape CHECK (id ~ '^[0-9a-f]{24}$'),
    CONSTRAINT oauth_grant_scope CHECK (scope = 'read'),
    CONSTRAINT oauth_grant_revoked_with_reason CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL)),
    CONSTRAINT oauth_grant_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX oauth_grant_user_idx ON prem_config.oauth_grant(tenant_id, user_id);
CREATE INDEX oauth_grant_client_idx ON prem_config.oauth_grant(tenant_id, client_id);
CREATE INDEX oauth_grant_agent_idx ON prem_config.oauth_grant(agent_id);

CREATE TABLE prem_config.oauth_code(
    code_sha256 BYTEA PRIMARY KEY,
    tenant_id UUID NOT NULL,
    grant_id TEXT NOT NULL REFERENCES prem_config.oauth_grant(id),
    client_id TEXT NOT NULL,
    redirect_uri TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    resource TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ,
    CONSTRAINT oauth_code_challenge_once UNIQUE (tenant_id, client_id, code_challenge),
    CONSTRAINT oauth_code_hash_length CHECK (octet_length(code_sha256) = 32),
    CONSTRAINT oauth_code_challenge_shape CHECK (code_challenge ~ '^[A-Za-z0-9_-]{43}$'),
    CONSTRAINT oauth_code_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX oauth_code_grant_idx ON prem_config.oauth_code(grant_id);
CREATE INDEX oauth_code_expires_idx ON prem_config.oauth_code(tenant_id, expires_at);

CREATE TABLE prem_config.oauth_refresh_token(
    id TEXT PRIMARY KEY,
    tenant_id UUID NOT NULL,
    grant_id TEXT NOT NULL REFERENCES prem_config.oauth_grant(id),
    secret_sha256 BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ,
    replaced_by TEXT,
    CONSTRAINT oauth_refresh_token_id_shape CHECK (id ~ '^[0-9a-f]{24}$'),
    CONSTRAINT oauth_refresh_token_hash_length CHECK (octet_length(secret_sha256) = 32),
    CONSTRAINT oauth_refresh_token_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX oauth_refresh_token_grant_idx ON prem_config.oauth_refresh_token(grant_id);
CREATE INDEX oauth_refresh_token_expires_idx ON prem_config.oauth_refresh_token(tenant_id, expires_at);

CREATE TABLE prem_config.oauth_access_token(
    id TEXT PRIMARY KEY,
    tenant_id UUID NOT NULL,
    grant_id TEXT NOT NULL REFERENCES prem_config.oauth_grant(id),
    secret_sha256 BYTEA NOT NULL,
    audience TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT oauth_access_token_id_shape CHECK (id ~ '^[0-9a-f]{24}$'),
    CONSTRAINT oauth_access_token_hash_length CHECK (octet_length(secret_sha256) = 32),
    CONSTRAINT oauth_access_token_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX oauth_access_token_grant_idx ON prem_config.oauth_access_token(grant_id);
CREATE INDEX oauth_access_token_expires_idx ON prem_config.oauth_access_token(tenant_id, expires_at);
