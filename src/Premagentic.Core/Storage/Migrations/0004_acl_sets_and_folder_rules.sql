-- Access lists, the operator's folder rules, and the move of the document gate
-- onto access-list ids. From here on a document has exactly one statement of
-- who may read it: its acl_set_id. The two principal columns the baseline
-- carried are dropped in the same step, so there is never a second one.

-- schema: prem_config

-- One row per distinct ordered access list per tenant, in its canonical text.
-- A row is never updated or deleted. Identical lists share one id, so editing
-- a row in place would change every folder that shares it; a permission change
-- points documents at another row instead.
CREATE TABLE prem_config.acl_set(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    sha256 TEXT NOT NULL,
    canonical_text TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(tenant_id, sha256),
    UNIQUE(tenant_id, id),
    CONSTRAINT acl_set_sha256_shape CHECK (sha256 ~ '^[0-9a-f]{64}$')
);

-- Every document from source whose path is path_prefix or lies beneath it, in
-- whole segments ('' is the whole source), gets acl_set_id, unless a longer
-- prefix also matches. A change retires the live row and inserts a new one, so
-- the history of who could read a folder, and from when, is kept.
CREATE TABLE prem_config.folder_rule(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    source TEXT NOT NULL,
    path_prefix TEXT NOT NULL,
    acl_set_id BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ,
    FOREIGN KEY(tenant_id, acl_set_id) REFERENCES prem_config.acl_set(tenant_id, id)
);
CREATE UNIQUE INDEX folder_rule_live_idx
    ON prem_config.folder_rule(tenant_id, source, path_prefix) WHERE deleted_at IS NULL;

-- schema: prem_index

-- The gate is d.acl_set_id = ANY(@permitted). A NULL acl_set_id equals
-- nothing, so it reaches nobody. acl_from_rule marks a document whose list came
-- from the folder rules rather than from its connector; only those move when a
-- rule changes.
DROP INDEX prem_index.document_principals_idx;
ALTER TABLE prem_index.document
    DROP COLUMN is_public,
    DROP COLUMN allowed_principals,
    ADD COLUMN acl_set_id BIGINT,
    ADD COLUMN acl_from_rule BOOLEAN NOT NULL DEFAULT false,
    ADD FOREIGN KEY(tenant_id, acl_set_id) REFERENCES prem_config.acl_set(tenant_id, id);
CREATE INDEX document_acl_set_idx ON prem_index.document(acl_set_id);
CREATE INDEX document_rule_scope_idx
    ON prem_index.document(tenant_id, source_name, path) WHERE acl_from_rule;
