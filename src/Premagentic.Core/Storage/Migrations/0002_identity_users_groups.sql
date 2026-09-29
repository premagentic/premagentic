-- Local accounts and groups.
--
-- Every row belongs to one tenant. Ids are generated once and never reused: a
-- delete sets deleted_at and leaves the row, because access rules name ids,
-- and a reused id would inherit whatever the old one was granted or denied.
-- Names are unique only among live rows, so a deleted name can be taken again
-- by a new row with a new id, which inherits nothing.
--
-- The composite (tenant_id, id) keys let a membership refer to a user and a
-- group of the same tenant only.

-- schema: prem_config

CREATE TABLE prem_config.app_user(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    sign_in_name TEXT NOT NULL,
    display_name TEXT NOT NULL,
    role TEXT NOT NULL,
    disabled BOOLEAN NOT NULL DEFAULT false,
    -- A version 1 PBKDF2 string, or NULL for an account with no local password.
    password_hash TEXT,
    -- Sign-in throttling, for when Premagentic signs people in itself.
    failed_sign_ins INT NOT NULL DEFAULT 0,
    locked_until TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ,
    UNIQUE(tenant_id, id),
    CONSTRAINT app_user_role_known CHECK (role IN ('administrator', 'auditor', 'member')),
    CONSTRAINT app_user_sign_in_name_trimmed CHECK (sign_in_name <> '' AND sign_in_name = btrim(sign_in_name))
);
-- Sign-in names compare case-insensitively, among live accounts only.
CREATE UNIQUE INDEX app_user_live_sign_in_name_idx
    ON prem_config.app_user(tenant_id, lower(sign_in_name)) WHERE deleted_at IS NULL;

-- name is for people to read. Access rules name the id, so renaming a group
-- never changes what a rule means, and a deny entry cannot be disarmed by it.
CREATE TABLE prem_config.app_group(
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at TIMESTAMPTZ,
    UNIQUE(tenant_id, id),
    CONSTRAINT app_group_name_trimmed CHECK (name <> '' AND name = btrim(name))
);
CREATE UNIQUE INDEX app_group_live_name_idx
    ON prem_config.app_group(tenant_id, lower(name)) WHERE deleted_at IS NULL;

CREATE TABLE prem_config.group_membership(
    tenant_id UUID NOT NULL,
    group_id UUID NOT NULL,
    user_id UUID NOT NULL,
    added_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY(group_id, user_id),
    FOREIGN KEY(tenant_id, group_id) REFERENCES prem_config.app_group(tenant_id, id),
    FOREIGN KEY(tenant_id, user_id) REFERENCES prem_config.app_user(tenant_id, id)
);
CREATE INDEX group_membership_user_idx ON prem_config.group_membership(user_id);
