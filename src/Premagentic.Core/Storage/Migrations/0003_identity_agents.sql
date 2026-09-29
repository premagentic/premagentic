-- Registered agents, the groups granted to service agents, and agent tokens.
--
-- Agents read and never write, so there is no scope to store beyond who they
-- read as. An agent that acts for a user reads as its owner, narrowed by any
-- entry that names the agent; a service agent reads as itself plus the groups
-- granted here. Either stops when its owner is disabled.

-- schema: prem_config

CREATE TABLE prem_config.agent(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    name TEXT NOT NULL,
    owner_user_id UUID NOT NULL,
    mode TEXT NOT NULL,
    disabled BOOLEAN NOT NULL DEFAULT false,
    requests_per_minute INT NOT NULL,
    -- NULL follows the deployment's trust policy.
    minimum_trust_tier TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ,
    UNIQUE(tenant_id, id),
    FOREIGN KEY(tenant_id, owner_user_id) REFERENCES prem_config.app_user(tenant_id, id),
    CONSTRAINT agent_mode_known CHECK (mode IN ('acts_for_user', 'service')),
    CONSTRAINT agent_rate_positive CHECK (requests_per_minute > 0),
    CONSTRAINT agent_name_trimmed CHECK (name <> '' AND name = btrim(name))
);
CREATE UNIQUE INDEX agent_live_name_idx
    ON prem_config.agent(tenant_id, lower(name)) WHERE deleted_at IS NULL;

-- Read only for service agents. An agent that acts for a user holds that
-- user's groups at the moment of each call instead.
CREATE TABLE prem_config.agent_group_grant(
    tenant_id UUID NOT NULL,
    agent_id UUID NOT NULL,
    group_id UUID NOT NULL,
    granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(agent_id, group_id),
    FOREIGN KEY(tenant_id, agent_id) REFERENCES prem_config.agent(tenant_id, id),
    FOREIGN KEY(tenant_id, group_id) REFERENCES prem_config.app_group(tenant_id, id)
);

-- Only the SHA-256 of a token's secret is stored. The token itself is shown
-- once, when it is issued, and cannot be recovered from this table.
CREATE TABLE prem_config.agent_token(
    id TEXT PRIMARY KEY,
    tenant_id UUID NOT NULL,
    agent_id UUID NOT NULL,
    secret_sha256 BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    last_used_at TIMESTAMPTZ,
    FOREIGN KEY(tenant_id, agent_id) REFERENCES prem_config.agent(tenant_id, id),
    CONSTRAINT agent_token_id_shape CHECK (id ~ '^[0-9a-f]{24}$'),
    CONSTRAINT agent_token_hash_length CHECK (octet_length(secret_sha256) = 32),
    CONSTRAINT agent_token_expires_after_created CHECK (expires_at > created_at)
);
CREATE INDEX agent_token_agent_idx ON prem_config.agent_token(agent_id);
