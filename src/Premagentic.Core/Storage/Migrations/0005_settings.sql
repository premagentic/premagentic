-- Per-tenant settings an administrator can change: one JSON value per key.

-- schema: prem_config

CREATE TABLE prem_config.setting(
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    key TEXT NOT NULL,
    value JSONB NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(tenant_id, key),
    CONSTRAINT setting_key_shape CHECK (key ~ '^[a-z0-9_.]+$')
);
