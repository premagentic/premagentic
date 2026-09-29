-- The profile a deployment was configured from, append only.
--
-- A profile is a folder of plain files that carries a whole configuration:
-- settings, sources and their rules. Applying one writes every change through
-- the ordinary administrator paths, so the change record already holds what
-- changed. This table answers a different question, the one a person asks
-- first: what is this deployment configured from, and when was it last done.
--
-- It is its own table rather than a search of the change record because the
-- change record is an audit trail and audit trails are pruned. What a
-- deployment is configured from must not age out.
--
-- Append only, one row per apply, the newest row winning. A profile reapplied
-- at a new version leaves both rows, which is the history an operator wants
-- when a deployment stopped matching what they thought was on it.
--
--   name        The profile's name, as profile.json gives it.
--   version     Its version, as profile.json gives it. Any text, as long as it
--               changes when the profile does.
--   applied_by  Who applied it, described the way the change record describes
--               an actor. The change record holds the full actor; this is for
--               reading.

-- schema: prem_config

CREATE TABLE prem_config.profile_applied(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES prem_config.tenant(id),
    name TEXT NOT NULL,
    version TEXT NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    applied_by TEXT NOT NULL,
    CONSTRAINT profile_applied_name_present CHECK (length(btrim(name)) > 0),
    CONSTRAINT profile_applied_version_present CHECK (length(btrim(version)) > 0)
);
CREATE INDEX profile_applied_tenant_idx ON prem_config.profile_applied(tenant_id, id DESC);

CREATE FUNCTION prem_config.profile_applied_is_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'prem_config.profile_applied is append only: applying a profile adds a row, and nothing edits or removes one';
END;
$$;

CREATE TRIGGER profile_applied_no_update_or_delete
    BEFORE UPDATE OR DELETE ON prem_config.profile_applied
    FOR EACH ROW EXECUTE FUNCTION prem_config.profile_applied_is_append_only();

CREATE TRIGGER profile_applied_no_truncate
    BEFORE TRUNCATE ON prem_config.profile_applied
    FOR EACH STATEMENT EXECUTE FUNCTION prem_config.profile_applied_is_append_only();
